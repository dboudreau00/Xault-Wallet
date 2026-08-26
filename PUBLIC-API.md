# XaultWallet.Core — public API for integrators

`XaultWallet.Core` is a standalone .NET 8 class library with no UI dependencies. Other
applications can reference it directly to get the vault format, duress logic, and the
monero-wallet-rpc orchestration without the Avalonia desktop app.

> ⚠️ Same caveat as the app: **unaudited beta**. Do not build anything holding real funds on
> this until it has had a professional security review. See `SECURITY.md`.

## Namespaces & surface

### `XaultWallet.Core.Security`
- **`VaultManager`** — create/load/unlock the two-slot vault file.
  - `Create(path, mainPassword, mainSecrets, duressPassword?, duressSecrets?)` — rejects an
    empty main password, a duress password equal to the main password, the same mnemonic in
    both slots, and a half-specified duress profile (all `ArgumentException`): any of those
    would silently break unlock determinism or the duress feature itself.
  - `Load(path)` / `Exists(path)`
  - `Unlock(password)` → returns the decrypted `WalletSecrets` for whichever slot the password
    opens (real or duress), or null. **No plaintext password comparison exists anywhere** — a
    password "matches" only by successfully authenticating a slot's AES-GCM tag.
  - `ChangeMainPassword(current, new)` — re-encrypts the real slot (rejects the duress
    password). Throws `ArgumentException` if the new password would ALSO open the other slot —
    that would recreate the ambiguous-unlock hazard `Create` guards against. Exposed in the
    desktop Settings screen.
  - `ChangeDaemonAddress(password, newDaemonAddress)` — repoints an existing wallet at a new
    node and re-seals **whichever slot the password opens** (real *or* duress), so the operation
    reveals nothing about which profile is which. Returns false on a wrong password; throws if the
    address isn't a valid http(s) URL. Exposed in the desktop Settings screen ("Change this
    wallet's node"). Takes effect on the next unlock.
- **`VaultCrypto`** — Argon2id key derivation (bounded params) + AES-256-GCM encrypt/decrypt,
  `RandomBytes`.
- **`VaultFile`** — the on-disk format (magic `XVLT`, v1, two equal padded slots, randomized
  slot order). `Serialize`/`Deserialize` with parameter validation.
- **`PasswordStrength`** — `Evaluate(password)` → `(StrengthLevel, bitsEstimate)`.
- **`SecureBuffer`** — pinned, zero-on-dispose byte buffer for passwords/keys.

### `XaultWallet.Core.Models`
- **`WalletSecrets`** — everything one wallet profile needs: `Mnemonic`, `RestoreHeight`,
  `DaemonAddress`, `Network`, `Kind` (`Real`/`Duress`), `DuressWipeReal`, `Label`,
  `EphemeralWalletPassword`, and `SeedOffset` (Monero seed-offset passphrase — honored by the
  restore pipeline; surfaced in the desktop create UI on the **import path only**).
- **`SeedOffsetPolicy`** — `ForSeed(wasGenerated, userOffset)` → the offset that is safe to seal.
  Empty for a generated seed (which must never carry an offset — that would restore a different,
  empty wallet), or the user's offset **byte-for-byte** for an imported seed (the offset is
  `cn_slow_hash`'d raw, so it is case- and whitespace-sensitive and must not be trimmed). This is
  the single choke point enforcing "an offset only ever accompanies an imported seed."
- **`MoneroNetwork`** — `Mainnet` / `Stagenet` / `Testnet`.

### `XaultWallet.Core.Monero`
- **`MoneroWalletService`** — the high-level entry point most integrators want.
  - `GenerateNewSeedAsync(network, daemon)` → fresh 25-word seed + current restore height
  - `ValidateSeedOpensAsync(secrets)` → opens a wallet once to confirm a seed is valid
  - `OpenAsync(secrets)` → restores into an ephemeral temp dir (shredded on close)
  - `GetBalanceAsync` / `GetHeightAsync` / `GetHistoryAsync` / `RefreshAsync`
  - `SendAsync(address, amountXmr, priority)` → `TransferResult` (includes `TxKey`) — builds AND
    broadcasts in one step
  - `PrepareSendAsync(address, amountXmr, priority)` → `TransferResult` with the **exact fee** and
    `TxMetadata`; builds the signed tx WITHOUT broadcasting (`do_not_relay`). Discarding the result
    cancels the send entirely — nothing touches the network until…
  - `RelaySendAsync(txMetadata)` → tx hash. Broadcasts a previously prepared tx; the fee shown at
    prepare time is baked into the signed tx and cannot change. (This prepare→confirm→relay pair is
    what the desktop send-confirm dialog uses.)
  - `PrepareSweepAllAsync(address, priority)` → `SweepAllResult` — builds transactions sweeping
    the ENTIRE spendable balance WITHOUT broadcasting (`do_not_relay`). A sweep can split into
    several transactions (parallel `AmountList`/`FeeList`/`TxMetadataList`); relay each metadata
    entry with `RelaySendAsync`. Same discard-cancels contract as `PrepareSendAsync`.
  - `BackendExited` — true when the wallet-rpc child died underneath an open wallet, so callers
    can offer a restart instead of surfacing repeated connection errors.
  - `GetTxKeyAsync(txid)` / `CheckTxKeyAsync(txid, txKey, address)` — payment proofs
  - `NewSubaddressAsync(label)`
  - `CloseAsync` / `DisposeAsync` — always dispose; this shreds the temp wallet files.
- **`MoneroProcessManager`** — lower-level: launches a loopback-only `monero-wallet-rpc`
  child on a random port with `--disable-rpc-login`, readiness-probes it, kills + shreds on
  dispose. On Windows the child is tied to the parent via a kill-on-close Job Object, so a
  crashed host can't leave wallet-rpc serving the open wallet. An optional constructor
  `proxyAddress` ("host:port") routes daemon traffic through a SOCKS proxy (`--proxy`, e.g.
  Tor). `ShredOrphanedTempDirs()` sweeps leftovers from a crashed previous session — call once
  at startup, only under a single-instance guard. Use `MoneroWalletService` unless you need
  custom lifecycle control.
- **`MoneroRpcClient`** — thin JSON-RPC client (hand-built envelope; omits null `params`).
  Typed wrappers for the methods above. `AtomicToXmr`/`XmrToAtomic` helpers (`XmrToAtomic`
  throws `ArgumentOutOfRangeException` for negative amounts or amounts above the total Monero
  supply, instead of a raw `OverflowException`).
- **`MoneroAddress`** — `Problem(address, network)` → null or a human-readable reason.
  Sanity-level only (charset/length/prefix); checksum authority stays with monero-wallet-rpc.
- **`MoneroDiagnostics`** — `ProbeWalletRpcAsync(binaryPath)` (runs `--version`),
  `ProbeDaemonAsync(daemonUrl)` (GET `/get_height`).

### `XaultWallet.Core.Diagnostics`
- **`Log`** — `Initialize(dir)`, `Info/Warn/Error`. Thread-safe file logger; never log secrets.

## Lifetime & threading notes
- `MoneroWalletService` / `MoneroProcessManager` own a child process — **always** dispose
  (`await using`), including on failure paths, or you leak an RPC process and temp files.
- All async methods accept a `CancellationToken`; cancellation kills in-flight RPC calls but
  still cleans up on dispose.
- The vault file is written atomically (temp + fsync + rename); concurrent writers are not
  supported — one process should own a vault at a time.
- Requires the external, user-verified `monero-wallet-rpc` binary and a reachable `monerod`.
  Nothing Monero-cryptographic is reimplemented here by design.
