# Changelog

## 0.3.0-beta — 2026-10-04 — tested end to end, motion & polish

Everything below is new since 0.2.0-beta. **Coming from 0.1?** The vault format changed in 0.2.0-beta:
read its [notes](CHANGELOG.md#020-beta--2026-10-03--security-audit--redesign) and
[Upgrading from 0.1](SECURITY.md#upgrading-from-01) first. Audit notes for this release:
[docs/AUDIT-2026-10.md → Third pass](docs/AUDIT-2026-10.md#third-pass-end-to-end-testing).

### Tested end to end
- **The released Windows executable is tested on every change.** A new workflow unpacks the exact
  `win-x64` zip, puts the official Monero CLI next to it (its `hashes.txt` must be signed by
  binaryFate's key), starts a private regtest chain, and drives the app from outside through Windows
  UI Automation, the way a screen reader would: create a vault, verify the seed backup, set a duress
  password and decoy, unlock both wallets, receive, copy the address, send with the confirmation
  checked line by line, see it in History, see the decoy receive it, open Settings. It then closes the
  app with its own close button and fails if `monero-wallet-rpc` is still running, a wallet session
  folder is left in `%TEMP%`, or anything was written next to the exe. **The release workflow drafts
  nothing unless this passes on the zip it is about to attach.**
- The same scenario runs on Linux in CI, in-process with real pointer and keyboard input.
- A node on this computer that reports itself as a private test chain (`monerod --regtest`) is
  recognised: it is labelled *Regtest · local test chain* instead of mainnet, and wallet-rpc gets the
  flag such a chain needs. This never applies to a remote node or to mainnet, stagenet or testnet.

### Security
- **Password boxes no longer hand their text to other programs.** Avalonia's text box reports its
  content through UI Automation even when it is masked, so any program in your desktop session could
  read a vault password as you typed it, without hooks or injection. They now accept input from
  assistive tools but never return it.

### Fixed
- **Mining rewards** (P2Pool and solo payouts) were listed in History as *outgoing* "−35.12 XMR",
  titled "block", and counted to 10 confirmations. They are now "Mining reward", +amount, confirming
  over the 60-block window mined coins actually need.
- **Screen readers announced nothing for your balance**, and several buttons as
  "Avalonia.Controls.StackPanel". Every control you can operate now has a real name, and a test keeps
  it that way.
- Copying the transaction ID or key from the Send tab's payment proof gave no feedback.
- On a small laptop screen (1366×768 at 125 %) the bottom of the window was off-screen. The window now
  opens centred and fits the screen; the send confirmation scrolls if it has to.

### Changed
- **Motion.** Screens rise into place, sections arrive one after another, dialogs fade in over their
  backdrop, buttons give a little when pressed, the sync dot breathes while syncing, the refresh icon
  turns, your balance pulses when money arrives or leaves, and a wrong password shakes the card. It is
  off when Windows' *Animation effects* setting is off, or with `XAULTWALLET_REDUCE_MOTION=1`.
- **Send results are a card:** sent (with the fee, and when the money moves), *may have been
  broadcast — check History before sending again*, or not sent, each clearly coloured.
- Copy and export feedback is a toast at the bottom, on whichever tab you're on.
- Unlock says *Unlocking…* while your key is derived.

### For integrators of XaultWallet.Core
- No breaking changes. New: `DaemonAddress.IsLoopback`, `MoneroDiagnostics.IsLocalTestChainAsync`,
  `MoneroProcessManager.IsLocalTestChain`, `MoneroWalletService.IsLocalTestChain`. See
  [PUBLIC-API.md](PUBLIC-API.md).

## 0.2.0-beta — 2026-10-03 — security audit & redesign

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
- Linux: if `~/.config` didn't exist yet, the vault was created in whatever folder the app was
  started from, and seemed to vanish when it was started elsewhere. It now always lives in
  `~/.config/XaultWallet` (or `$XDG_CONFIG_HOME/XaultWallet`).
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
