# XaultWallet.Core — public API for integrators

`XaultWallet.Core` is a standalone .NET 8 class library with no UI dependencies. Other
applications can reference it directly to get the vault format, duress logic, and the
monero-wallet-rpc orchestration without the Avalonia desktop app.

> ⚠️ Same caveat as the app: **unaudited beta**. Do not build anything holding real funds on
> this until it has had a professional security review. See `SECURITY.md`.
>
> 0.2.0 has breaking changes from 0.1.0 — see [CHANGELOG.md](CHANGELOG.md#breaking-integrators-of-xaultwalletcore).
> 0.3.0 only adds API (local regtest detection); nothing existing changed.
> **0.5.0 has breaking changes:** a password now opens a `WalletProfile` (several wallets and an
> address book), changes are saved through a `VaultSession`, and `ChangeDaemonAddress` is gone. See
> [CHANGELOG.md](CHANGELOG.md#for-integrators-of-xaultwalletcore).

## Namespaces & surface

### `XaultWallet.Core.Security`
- **`VaultManager`** — create/load/unlock the two-slot vault file. Each slot holds one *profile*
  (`WalletProfile`: wallets + contacts). **Every operation is symmetric:** nothing distinguishes the
  decoy from the real profile, and no method reports which slot opened.
  - `Create(path, mainPassword, mainSecrets, duressPassword?, duressSecrets?, argon?)` — one wallet per
    profile; and `Create(path, mainPassword, mainProfile, duressPassword?, duressProfile?, argon?)`.
    Both reject an empty main password, a duress password equal to the main password, the same wallet
    (mnemonic or address) in both profiles, a half-specified duress profile, and
    `WipeOtherSlotOnUnlock` on the main profile (all `ArgumentException`).
  - `Load(path)` / `Exists(path)`
  - `Unlock(password)` → `UnlockResult(Profile, UpgradedFromLegacyFormat)` for whichever slot the
    password opens, or null (`Secrets` = the profile's active wallet). A password "matches" only by
    authenticating a slot's AES-GCM tag — no plaintext comparison exists. Applies the opened slot's
    policy before returning: wipe-on-duress, and re-sealing an older payload (v1, v2) or a slot carried
    over from a format 1 file as a v3 profile. `UpgradedFromLegacyFormat` is true on the unlock that
    re-sealed a payload written by 0.1, for either slot alike; show the same notice for both. Opening a
    current vault never writes the file.
  - `OpenSession(password)` → a `VaultSession` for whichever slot opens (the same policy as `Unlock`),
    or null. **This is how changes are saved.**
  - `ChangePassword(current, new)` — re-seals whichever slot `current` opens under `new`. Returns
    false on a wrong password; throws `ArgumentException` if `new` is empty or would also open the
    OTHER slot (neutral message).
  - Wipe-on-duress fires on **any** of these operations when the opened profile carries
    `WipeOtherSlotOnUnlock`: the other slot becomes random bytes, the flag is cleared (re-sealed with
    the already-derived key — no extra Argon2 time), and `<vault>.replaced-*` copies beside the vault
    are shredded. It is best-effort and silent on unlock; a failed write never changes what the caller sees.
- **`VaultSession`** (`IDisposable`) — an open profile. Change `Profile`, then `Save()` (or
  `SaveAsync()`: serializes on the calling thread, encrypts and writes in the background). A save
  re-reads the file and re-seals **only this slot** with the key derived at unlock; the other slot is
  written back byte for byte. `CheckPassword(password)` / `ChangePassword(current, new)` act on this
  slot only (false for the other slot's password, too). Throws `VaultFullException` (an `IOException`)
  when the profile no longer fits; nothing is written. `Dispose()` zeroes the key and waits for a save
  in progress; a save after that throws `ObjectDisposedException`.
- **`VaultFile`** — the on-disk container: magic `XVLT`, format 2, two equal slots of
  `PaddedPlaintextBytes` (256 KiB) each, randomized slot order, `FileBytes` in all. `Deserialize` also
  reads format 1 (0.2/0.3: 4 KiB slots), carrying each old slot byte for byte into a new slot until its
  own password re-seals it; a newer format is refused. `Serialize` always writes format 2. `WriteSlot`
  (fresh salt), `FillRandom`, `TryUnlock` (payload + slot index), and `TryOpen` → `OpenedSlot` (payload,
  derived key, `IsLegacyLayout`, for `ResealSlot` with a fresh nonce). Every slot is tried both ways on
  every open, so a carried slot costs the same work. Dispose `OpenedSlot` promptly.
- **`VaultCrypto`** — Argon2id key derivation (bounded params) + AES-256-GCM encrypt/decrypt, `RandomBytes`.
- **`PasswordStrength`** — `Evaluate(password)` → `(StrengthLevel, bitsEstimate)`, discounting repeats,
  sequences, keyboard runs and very common passwords. `MinimumAccepted` is the floor the app enforces.
- **`SecureBuffer`** — pinned, zero-on-dispose byte buffer for passwords/keys.
- **`PrivateFiles`** — owner-only file I/O on Linux/macOS (no-op on Windows): `EnsureDirectory` (`0700`,
  tightening an existing folder), `OpenWrite` / `WriteAllBytes` / `WriteAllText` (`0600`; an existing
  file is tightened before it is written), `AppendAllText`, `Tighten`, and `TightenTree` (startup sweep;
  never follows symlinks). The vault, settings and log writers all go through it.

The sealed payload (internal `SlotPayload`) is identical in shape for every slot:
`{"v":3,"wallets":[{"id","name","type","network","mnemonic","seedOffset","address","viewKey","spendKey","restoreHeight","daemonAddress","ephemeralWalletPassword","subaddresses","labels","accountLabels","notes"}…],"contacts":[{"id","name","address","note"}…],"active":…,"wipeOther":…}`.
(A wallet's kind is `type`; 0.1's real/decoy marker was `kind`, and nothing like it exists.) v1 and v2
payloads are read and become a one-wallet profile; a `v` newer than 3, or a profile without wallets,
is refused with `InvalidDataException`.

### `XaultWallet.Core.Models`
- **`WalletProfile`** — what one password opens: `Wallets`, `Contacts`, `ActiveWalletId` (opens
  first), `WipeOtherSlotOnUnlock` (input when creating a duress profile), `ActiveWallet`, and
  `OfOne(wallet)` for a one-wallet profile.
- **`WalletSecrets`** — one wallet: `Id`, `Name`, `Kind` (`WalletKind.Seed` / `Keys` / `ViewOnly`),
  `Network`, `Mnemonic` + `SeedOffset` (seed), `Address` + `ViewKey` (+ `SpendKey`) (keys,
  watch-only), `RestoreHeight`, `DaemonAddress`, `EphemeralWalletPassword`, and the wallet's own
  `SubaddressCounts` (per account, so a restored wallet re-creates what it handed out), `Labels`
  (`"account/index"` → label), `AccountLabels` and `TxNotes`. `CanSpend` is false for watch-only.
  Nothing in it says real or decoy.
- **`Contact`** — `Id`, `Name`, `Address`, `Note`.
- **`SeedOffsetPolicy`** — `ForSeed(wasGenerated, userOffset)` → the offset that is safe to seal: empty
  for a generated seed, the user's offset **byte-for-byte** for an imported one.
- **`MoneroNetwork`** — `Mainnet` / `Stagenet` / `Testnet`.

### `XaultWallet.Core.Monero`
- **`MoneroWalletService`** — the high-level entry point most integrators want.
  - `new MoneroWalletService(binary, proxyAddress?)` or `new MoneroWalletService(binary, WalletRpcOptions)`
  - `GenerateNewSeedAsync(network, daemon)` → 25-word seed + restore height from the daemon's tip read
    **before** the seed exists, via `RestoreHeights.ForNewSeed` (capped by the clock, minus
    `GeneratedSeedRestoreMargin` = 720), or 0 if the node is unreachable. The backend's ownership is
    checked before `create_wallet`.
  - `ValidateWalletOpensAsync(secrets)` → opens a wallet once (from seed or keys) and returns its
    primary address. For keys it also proves they belong to the address: `generate_from_keys` doesn't
    check, so the wallet is reopened, where wallet2's key check runs; a mismatch throws, naming the key.
    (`ValidateSeedOpensAsync` is the same method under its old name.)
  - `OpenAsync(secrets)` → restores into a private session directory (shredded on close), then
    re-creates the accounts and subaddresses recorded in `SubaddressCounts`
  - `GetBalanceAsync(account)` / `GetPrimaryAddressAsync` / `GetHeightAsync` / `GetHistoryAsync(account)` / `RefreshAsync`
  - Accounts and addresses: `GetAccountsAsync`, `NewAccountAsync`, `GetAddressesAsync(account)` (with
    `Used`), `NewSubaddressAsync(account, label)` → (index, address)
  - `PrepareSendAsync(destinations, account, priority)` → `TransferResult` with the **exact fee**,
    `TxKey` and `TxMetadata`; builds the signed tx WITHOUT broadcasting (up to `MaxDestinations` = 15
    recipients; a transaction has at most 16 outputs). Discarding it cancels the send.
    `PrepareSendAsync(address, amountXmr, priority)` is the one-recipient form.
  - `RelaySendAsync(txMetadata)` → tx hash. Broadcasts a prepared tx; its fee cannot change.
  - `PrepareSweepAllAsync(address, account, priority)` → `SweepAllResult` (parallel lists; relay each metadata entry)
  - `SendAsync(address, amountXmr, priority)` — builds AND broadcasts in one step (no review); prefer prepare/relay
  - Proofs: `GetTxKeyAsync(txid)` (throws if the backend returns no key) / `CheckTxKeyAsync(txid, txKey, address)`;
    `SignMessageAsync(message)` (main address) / `VerifyMessageAsync(message, address, signature)`;
    `GetReserveProofAsync(amount?, account, message)` / `CheckReserveProofAsync(address, message, proof)` → (good, total, spent)
  - `SetDaemonAsync(daemon)` (switch node live, URL validated), `RescanSpentAsync`, `GetKeysAsync` → (mnemonic, viewKey, spendKey)
  - `BackendExited` — the wallet-rpc child died underneath an open wallet
  - `IsLocalTestChain` — the open wallet syncs from a private regtest chain on this machine (see
    `MoneroDiagnostics.IsLocalTestChainAsync`); label it as such rather than as its address network
  - `CloseAsync` / `DisposeAsync` — always dispose; this shreds the session directory.
- **`MoneroProcessManager`** — lower level: launches `monero-wallet-rpc` on a random loopback port with
  per-session random **Digest credentials** (written to a `0600` `--config-file` in a `0700` session
  directory, shredded once ready — never on argv), `--log-file` and `--shared-ringdb-dir` inside that
  directory, and `WorkingDirectory` set to it. `EnsureBackendIsOurs()` throws unless the child is alive
  and owns every listener that could answer on its port (Linux, Windows; fail-closed — "can't tell" is a
  no). It runs when the server first answers and again right before the seed is sent; call it before
  any other call that carries a secret. A 401 during readiness is a hard failure. The binary must be a
  fully-qualified path (`ExecutableLocator.EnsureLaunchable`). On Windows the child
  is tied to the parent with a kill-on-close Job Object. `ShredOrphanedTempDirs()` sweeps leftovers from
  a crashed session — call once at startup under a single-instance guard. Each launch asks the node
  whether it is a local test chain; if so (`IsLocalTestChain`), it passes
  `--allow-mismatched-daemon-version`, which regtest needs.
- **`WalletRpcOptions`** — `ProxyAddress` ("host:port" SOCKS for daemon traffic, e.g. Tor) and
  `AllowMismatchedDaemonVersion` (**testing only**: forces the flag for any node. A local
  `monerod --regtest` node doesn't need it; it is detected).
- **`MoneroRpcClient`** — thin JSON-RPC client (hand-built envelope; omits null `params`). Credentials
  are offered only to a `Digest` challenge and never through a proxy. Errors are
  `MoneroRpcException` (`Code`; `Message` = method + backend error, safe to show and log;
  `Diagnostics` = redacted request/response, kept out of logs because it can identify a wallet).
  `AtomicToXmr` / `XmrToAtomic` (range-checked).
- **`XmrAmount`** — `TryParse(text, out xmr, out error)`: culture-independent; `.` or `,` is always the
  decimal point, never grouping, so ambiguous input can only parse lower than intended. `LooksThousandsGrouped`,
  `Format` (invariant, up to 12 decimals).
- **`BlockHeight`** — `TryParse(text, out height)`: plain digits, or groups of three behind one kind
  of separator (`3,150,000`, `3.150.000`, `3 150 000`); anything else (`3150000.0`, `31,50,000`) is refused.
- **`RestoreHeights`** — `ApproximateTip(network, now)`: monero's clock-based chain estimate
  (`wallet2::get_approximate_blockchain_height`, ported from upstream). `ForNewSeed(reportedTip, network, now)`
  = `min(reportedTip, estimate) − SafetyMargin` (720), floored at 0: a node can't push a new seed's start
  past the clock.
- **`ExecutableLocator`** — `FindOnPath(fileName, pathVariable)` → absolute path or null (skips relative
  entries). `ResolveConfigured(text, pathVariable)`: keeps a full path, looks a bare name up on PATH,
  passes anything else through unchanged. `EnsureLaunchable(path)` throws `FileNotFoundException` unless
  the path is fully qualified and exists.
- **`MoneroAddress`** — `Problem(address, network)` → null or a human-readable reason (charset/length/prefix only).
- **`MoneroUri`** — `monero:` payment links. `Build(MoneroPaymentRequest)` (address, optional
  `tx_amount`, `tx_description`, `recipient_name`); `IsUri(text)`; `TryParse(uri, out problem)` →
  `MoneroPaymentRequest` or null with a reason. Strict: an amount is digits with an optional `.` and
  up to 12 decimals (`1,5` is refused, never read as 1.5 or 15), and links with `tx_payment_id` or
  several addresses are refused.
- **`DaemonAddress`** — the one definition of a valid node URL. `IsLoopback(address)`: `localhost` or a
  loopback IP (`127.0.0.0/8`, `::1`).
- **`MoneroDiagnostics`** — `ProbeWalletRpcAsync(binaryPath)` (runs `--version`; same full-path rule as a launch),
  `ProbeDaemonAsync(daemonUrl, proxy)` (GET `/get_height`, same route as wallet-rpc: SOCKS or direct),
  `IsLocalTestChainAsync(daemonUrl, proxy)`: true only for a **loopback** node whose `/get_info` reports
  `nettype: "fakechain"` (what `monerod --regtest` runs). A remote node is never asked; any failure
  (unreachable, not JSON, an odd answer) is `false`. 5 s timeout.
- **`SecretRedactor`** — structural JSON redaction of seeds, passwords, keys and signed-tx blobs.

### `XaultWallet.Core.Installer`
- **`WalletRpcInstaller`** — `new WalletRpcInstaller(installRoot, proxyAddress?)`;
  `InstallAsync(progress?, ct)` → `InstalledWalletRpc(Path, Version, VersionLine)`: fetches
  `OfficialHashListUrl` (getmonero.org `hashes.txt`), verifies its clearsigned OpenPGP signature
  against `MoneroSigningKeys.Trusted`, picks this platform's CLI archive from the signed list,
  downloads it from `OfficialDownloadBase`, requires the signed SHA-256, extracts only
  `monero-wallet-rpc`, runs `--version`, and moves it to `<installRoot>/v<version>/` (older versions
  removed). Every failure is a `WalletRpcInstallException` with a message fit to show, and leaves
  nothing installed. `FindInstalled(installRoot)` → the installed binary, or null.
  `InstallProgress(Stage, BytesDone, BytesTotal)` reports `InstallStage`.
- **`MoneroSigningKeys`** — `Trusted`: binaryFate's RSA keys, read from the copy of Monero's
  `binaryfate.asc` embedded in the assembly and accepted only if they are exactly the pinned
  `BinaryFatePrimary` and `BinaryFateSubkey` fingerprints.
- **`MoneroReleaseList`** — `Parse(signedLines)` → `MoneroCliArchive(FileName, Platform, Version, Sha256)`
  for the CLI builds; `Select(signedLines, platform)`; `CurrentPlatform()` / `PlatformName(os, arch)`
  (`win-x64`, `linux-x64`, `linux-armv8`, `mac-armv8`…); `WalletRpcFileName(platform)`.
- `PgpRsaKey` (a key's `Fingerprint`) and `SignatureCheckException` are public for those; the OpenPGP
  reader itself is internal.

### `XaultWallet.Core.Diagnostics`
- **`Log`** — `Initialize(dir)`, `Info/Warn/Error`. Thread-safe file logger. Never log secrets — or
  anything that differs between the two wallets of a vault (nodes, restore heights, send events).

## Lifetime & threading notes
- `MoneroWalletService` / `MoneroProcessManager` own a child process — **always** dispose
  (`await using`), including on failure paths, or you leak an RPC process and its session directory.
- All async methods accept a `CancellationToken`; cancellation stops in-flight RPC calls but still
  cleans up on dispose.
- The vault file is written atomically (temp + fsync + rename); one process should own a vault at a time.
- Requires the external, user-verified `monero-wallet-rpc` binary and a reachable `monerod`.
  Nothing Monero-cryptographic is reimplemented here by design.
