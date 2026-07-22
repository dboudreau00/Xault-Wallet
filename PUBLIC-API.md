# XaultWallet.Core — public API for integrators

`XaultWallet.Core` is a standalone .NET 8 class library with no UI dependencies. Other
applications can reference it directly to get the vault format, duress logic, and the
monero-wallet-rpc orchestration without the Avalonia desktop app.

> ⚠️ Same caveat as the app: **unaudited beta**. Do not build anything holding real funds on
> this until it has had a professional security review. See `SECURITY.md`.

## Namespaces & surface

### `XaultWallet.Core.Security`
- **`VaultManager`** — create/load/unlock the two-slot vault file.
  - `Create(path, mainPassword, mainSecrets, duressPassword?, duressSecrets?)`
  - `Load(path)` / `Exists(path)`
  - `Unlock(password)` → returns the decrypted `WalletSecrets` for whichever slot the password
    opens (real or duress), or null. **No plaintext password comparison exists anywhere** — a
    password "matches" only by successfully authenticating a slot's AES-GCM tag.
  - `ChangeMainPassword(current, new)` — re-encrypts the real slot (rejects the duress
    password). *Available in the API, not yet exposed in the desktop UI.*
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
  restore pipeline; not yet surfaced in the desktop UI).
- **`MoneroNetwork`** — `Mainnet` / `Stagenet` / `Testnet`.

### `XaultWallet.Core.Monero`
- **`MoneroWalletService`** — the high-level entry point most integrators want.
  - `GenerateNewSeedAsync(network, daemon)` → fresh 25-word seed + current restore height
  - `ValidateSeedOpensAsync(secrets)` → opens a wallet once to confirm a seed is valid
  - `OpenAsync(secrets)` → restores into an ephemeral temp dir (shredded on close)
  - `GetBalanceAsync` / `GetHeightAsync` / `GetHistoryAsync` / `RefreshAsync`
  - `SendAsync(address, amountXmr, priority)` → `TransferResult` (includes `TxKey`)
  - `GetTxKeyAsync(txid)` / `CheckTxKeyAsync(txid, txKey, address)` — payment proofs
  - `NewSubaddressAsync(label)`
  - `CloseAsync` / `DisposeAsync` — always dispose; this shreds the temp wallet files.
- **`MoneroProcessManager`** — lower-level: launches a loopback-only `monero-wallet-rpc`
  child on a random port with `--disable-rpc-login`, readiness-probes it, kills + shreds on
  dispose. Use `MoneroWalletService` unless you need custom lifecycle control.
- **`MoneroRpcClient`** — thin JSON-RPC client (hand-built envelope; omits null `params`).
  Typed wrappers for the methods above. `AtomicToXmr`/`XmrToAtomic` helpers.
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
