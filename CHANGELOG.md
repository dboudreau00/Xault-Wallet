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
- Before sending a seed to the backend, the app checks the backend's port belongs to the process it
  started (Linux, Windows).
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
- Typed restore heights like `2.800.000` are no longer silently ignored.
- Launching the app twice brings the open window forward (Windows).

### Changed
- Redesigned every screen: Receive QR code, a real transaction history, clearer send confirmation,
  step-by-step vault creation, regrouped Settings, show/hide password on unlock.
- New wallets default to **Stagenet**.
- Very weak passwords (common words, sequences, repeats) are refused.

### Breaking (integrators of XaultWallet.Core)
- `WalletSecrets`: `Kind`, `Label`, `DuressWipeReal` removed; `WipeOtherSlotOnUnlock` added; `ProfileKind` removed.
- `UnlockResult` no longer has `WasDuress`.
- `VaultManager.ChangeMainPassword` → `ChangePassword` (works for either slot).
- `MoneroRpcClient.GetTxKeyAsync` returns `GetTxKeyResult` (`TxKey`).
- `MoneroRpcException.Message` no longer embeds request/response bodies; see `Diagnostics`.
- New: `WalletRpcOptions`, `XmrAmount`, `BlockHeight`, `ExecutableLocator`, `VaultFile.TryOpen` / `ResealSlot`.

### Engineering
- CI on Linux and Windows. Integration tests run against the real monero-wallet-rpc on a private regtest
  chain (funds flow, RPC auth, file containment, duress), and a UI smoke test renders every screen and
  verifies the QR code decodes to the displayed address.
- Tag-triggered release pipeline: single-file builds for Windows, Linux and macOS with SHA256SUMS.
- Warnings are errors; deterministic builds.

## 0.1.0-beta
Initial public proof of concept.
