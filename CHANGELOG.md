# Changelog

## 0.2.0-beta — security audit & redesign

Full write-up with evidence: [docs/AUDIT-2026-10.md](docs/AUDIT-2026-10.md).

### Security
- **Wallet RPC now requires authentication.** Per-session random Digest credentials, passed through a
  private config file (not the command line) and shredded once the backend is up. Previously any web
  page could send it transactions, and DNS rebinding could read the seed.
- **The decoy can no longer be identified from its own contents.** Vault payload v2 has one identical
  field set for every slot. Old vaults keep working and are upgraded slot-by-slot as each password is used.
- **Change password is symmetric**, and no longer tells whoever is watching that a "real wallet" exists.
- **Wipe-on-duress** now fires on any use of the duress password, also destroys vault copies kept by
  Settings → Restore, and leaves the survivor looking like a single-wallet vault, at no extra unlock time.
- Before sending a seed to the backend, and before generating one, the app checks the backend's port
  belongs to the process it started and that it is still running (Linux, Windows). The check fails
  closed: an earlier version of this fix let the seed through when the hijacked backend had already
  exited.
- monero-wallet-rpc is only started from a full path; a bare name in Settings is looked up on PATH and
  a relative path is refused (it could otherwise run a file from the current directory).
- **Files are owner-only on Linux/macOS**: data and log folders `0700`; vault, settings, logs, vault
  exports, seed backups and history exports `0600`, whatever your umask. Existing installs are tightened
  at startup.
- A node can no longer push a new wallet's restore height past what the clock allows (which would
  hide incoming payments).
- wallet-rpc's log and ring database now live in the shredded session folder, not in your working
  directory and home folder.
- The app's log no longer records anything that could tell your two wallets apart.

### Fixed
- **Send amounts no longer depend on your system locale**: `0,25` could be sent as 25 XMR.
- New wallets no longer rescan the entire blockchain on every unlock.
- History showed 0 XMR for every transaction.
- Addresses could display `×` where the address has `x`.
- "Fetch key from wallet" (payment proofs) always returned an empty key.
- Leaving the monero-wallet-rpc path blank now really auto-detects it on your PATH.
- Typed restore heights like `2.800.000` are no longer silently ignored, and ambiguous ones like
  `3150000.0` are refused instead of being read 10× too high.
- A transaction's history row now updates when it reaches 10 confirmations.
- Launching the app twice brings the open window forward (Windows).

### Changed
- Redesigned every screen: Receive QR code, a real transaction history, clearer send confirmation,
  step-by-step vault creation, regrouped Settings, show/hide password on unlock.
- New wallets default to **Stagenet**.
- Very weak passwords (common words, sequences, repeats) are refused.

### Upgrading from 0.1
- Your vault keeps working. Each password's part of the vault is converted to the new format the first
  time that password is used, and the app says so once.
- **If you set a duress password in 0.1**, its decoy stays in the old format, which marks it as a decoy
  inside its encryption, until that password is used. If you enabled wipe-on-duress, using it also
  wipes your main wallet, as it always did. To remove the marker without that, rebuild the vault: see
  [SECURITY.md → Upgrading from 0.1](SECURITY.md#upgrading-from-01).

### Breaking (integrators of XaultWallet.Core)
- `WalletSecrets`: `Kind`, `Label`, `DuressWipeReal` removed; `WipeOtherSlotOnUnlock` added; `ProfileKind` removed.
- `UnlockResult` no longer has `WasDuress`; it gained `UpgradedFromLegacyFormat` (symmetric).
- `VaultManager.ChangeMainPassword` → `ChangePassword` (works for either slot).
- `MoneroRpcClient.GetTxKeyAsync` returns `GetTxKeyResult` (`TxKey`).
- `MoneroRpcException.Message` no longer embeds request/response bodies; see `Diagnostics`.
- `MoneroProcessManager` / `MoneroDiagnostics.ProbeWalletRpcAsync` refuse a binary path that isn't fully qualified.
- New: `WalletRpcOptions`, `XmrAmount`, `BlockHeight`, `RestoreHeights`, `ExecutableLocator`
  (`FindOnPath`, `ResolveConfigured`, `EnsureLaunchable`), `PrivateFiles`,
  `MoneroProcessManager.EnsureBackendIsOurs`, `VaultFile.TryOpen` / `ResealSlot`.

### Engineering
- CI on Linux and Windows. Integration tests run against the real monero-wallet-rpc on a private regtest
  chain (funds flow, RPC auth, file containment, duress), and a UI smoke test renders every screen and
  verifies the QR code decodes to the displayed address.
- Tag-triggered release pipeline: single-file builds for Windows, Linux and macOS with SHA256SUMS.
- Warnings are errors; deterministic builds.

## 0.1.0-beta
Initial public proof of concept.
