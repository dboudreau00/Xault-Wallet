# XaultWallet.Core — public API for integrators

`XaultWallet.Core` is a standalone .NET 8 class library with no UI dependencies. Other
applications can reference it directly to get the vault format, duress logic, and the
monero-wallet-rpc orchestration without the Avalonia desktop app.

> ⚠️ Same caveat as the app: **unaudited beta**. Do not build anything holding real funds on
> this until it has had a professional security review. See `SECURITY.md`.
>
> 0.2.0 has breaking changes from 0.1.0 — see [CHANGELOG.md](CHANGELOG.md#breaking-integrators-of-xaultwalletcore).
> 0.3.0 only adds API (local regtest detection); nothing existing changed.

## Namespaces & surface

### `XaultWallet.Core.Security`
- **`VaultManager`** — create/load/unlock the two-slot vault file. **Every operation is symmetric:**
  nothing distinguishes the decoy from the real wallet, and no method reports which slot opened.
  - `Create(path, mainPassword, mainSecrets, duressPassword?, duressSecrets?, argon?)` — rejects an
    empty main password, a duress password equal to the main password, the same mnemonic in both
    slots, a half-specified duress profile, and `WipeOtherSlotOnUnlock` on the main wallet (all
    `ArgumentException`).
  - `Load(path)` / `Exists(path)`
  - `Unlock(password)` → `UnlockResult(Secrets, UpgradedFromLegacyFormat)` for whichever slot the
    password opens, or null. A password "matches" only by authenticating a slot's AES-GCM tag — no
    plaintext comparison exists. Applies the opened slot's policy before returning: wipe-on-duress, and
    re-sealing a legacy (v1) payload as v2. `UpgradedFromLegacyFormat` is true on the unlock that did
    that re-seal, for either slot alike; show the same notice for both (see SECURITY.md → Upgrading from
    0.1). A plain unlock never writes the file.
  - `ChangePassword(current, new)` — re-seals whichever slot `current` opens under `new`. Returns
    false on a wrong password; throws `ArgumentException` if `new` is empty or would also open the
    OTHER slot (neutral message).
  - `ChangeDaemonAddress(password, newDaemonAddress)` — repoints whichever slot the password opens;
    throws on an invalid http(s) URL before any key derivation. Takes effect on the next unlock.
  - Wipe-on-duress fires on **any** of the three operations above when the opened slot carries
    `WipeOtherSlotOnUnlock`: the other slot becomes random bytes, the flag is cleared (re-sealed with
    the already-derived key — no extra Argon2 time), and `<vault>.replaced-*` copies beside the vault
    are shredded. It is best-effort and silent on unlock; a failed write never changes what the caller sees.
- **`VaultFile`** — the on-disk container (magic `XVLT`, v1 container, two equal padded slots,
  randomized slot order). `Serialize`/`Deserialize` with KDF-parameter validation; `WriteSlot`
  (fresh salt), `FillRandom`, `TryUnlock` (payload + slot index), and `TryOpen` → `OpenedSlot`
  (payload + the derived key, for `ResealSlot` with a fresh nonce). Dispose `OpenedSlot` promptly.
- **`VaultCrypto`** — Argon2id key derivation (bounded params) + AES-256-GCM encrypt/decrypt, `RandomBytes`.
- **`PasswordStrength`** — `Evaluate(password)` → `(StrengthLevel, bitsEstimate)`, discounting repeats,
  sequences, keyboard runs and very common passwords. `MinimumAccepted` is the floor the app enforces.
- **`SecureBuffer`** — pinned, zero-on-dispose byte buffer for passwords/keys.
- **`PrivateFiles`** — owner-only file I/O on Linux/macOS (no-op on Windows): `EnsureDirectory` (`0700`,
  tightening an existing folder), `OpenWrite` / `WriteAllBytes` / `WriteAllText` (`0600`; an existing
  file is tightened before it is written), `AppendAllText`, `Tighten`, and `TightenTree` (startup sweep;
  never follows symlinks). The vault, settings and log writers all go through it.

The sealed payload (internal `SlotPayload`) is identical in shape for every slot:
`{"v":2,"network":…,"mnemonic":…,"seedOffset":…,"restoreHeight":…,"daemonAddress":…,"ephemeralWalletPassword":…,"wipeOther":…}`.
v1 payloads (`kind` / `label` / `duressWipeReal`, no `v`) are read and migrated; a `v` newer than 2
is refused with `InvalidDataException`.

### `XaultWallet.Core.Models`
- **`WalletSecrets`** — everything one wallet needs: `Network`, `Mnemonic`, `SeedOffset`,
  `RestoreHeight`, `DaemonAddress`, `EphemeralWalletPassword`, `WipeOtherSlotOnUnlock` (decoy only).
  Deliberately no "kind" or label.
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
  - `ValidateSeedOpensAsync(secrets)` → opens a wallet once and returns its primary address
  - `OpenAsync(secrets)` → restores into a private session directory (shredded on close)
  - `GetBalanceAsync` / `GetPrimaryAddressAsync` / `GetHeightAsync` / `GetHistoryAsync` / `RefreshAsync`
  - `PrepareSendAsync(address, amountXmr, priority)` → `TransferResult` with the **exact fee**, `TxKey`
    and `TxMetadata`; builds the signed tx WITHOUT broadcasting. Discarding it cancels the send.
  - `RelaySendAsync(txMetadata)` → tx hash. Broadcasts a prepared tx; its fee cannot change.
  - `PrepareSweepAllAsync(address, priority)` → `SweepAllResult` (parallel lists; relay each metadata entry)
  - `SendAsync(address, amountXmr, priority)` — builds AND broadcasts in one step (no review); prefer prepare/relay
  - `GetTxKeyAsync(txid)` (throws if the backend returns no key) / `CheckTxKeyAsync(txid, txKey, address)` — payment proofs
  - `NewSubaddressAsync(label)`
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
- **`DaemonAddress`** — the one definition of a valid node URL. `IsLoopback(address)`: `localhost` or a
  loopback IP (`127.0.0.0/8`, `::1`).
- **`MoneroDiagnostics`** — `ProbeWalletRpcAsync(binaryPath)` (runs `--version`; same full-path rule as a launch),
  `ProbeDaemonAsync(daemonUrl, proxy)` (GET `/get_height`, same route as wallet-rpc: SOCKS or direct),
  `IsLocalTestChainAsync(daemonUrl, proxy)`: true only for a **loopback** node whose `/get_info` reports
  `nettype: "fakechain"` (what `monerod --regtest` runs). A remote node is never asked; any failure
  (unreachable, not JSON, an odd answer) is `false`. 5 s timeout.
- **`SecretRedactor`** — structural JSON redaction of seeds, passwords, keys and signed-tx blobs.

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
