# Changelog

## 0.7.0-beta - 2026-10-13 - browser companion, Enhanced Security

Two features, both off until you turn them on, and a round of interface fixes. **Your vault,
settings and wallets are unchanged**; nothing new happens until you use it.

### Browser companion (Chrome and Firefox)
- **An extension for Chrome and Firefox** ([docs/BROWSER-COMPANION.md](docs/BROWSER-COMPANION.md)). Its
  popup shows whether XaultWallet is running and unlocked, lists the `monero:` links and addresses you can
  see on the page you're on (naming the site, with the page's own words in quotes, and flagging plain
  `http` pages), opens one of them (or one you paste) in XaultWallet's Send tab, and gets a fresh receive
  address labelled with the site. Right-click a selected address: *Pay with XaultWallet*.
- **It holds nothing.** No key, seed, password, balance, wallet name or history ever reaches the browser,
  and the extension stores nothing (no storage permission, no network access, no content scripts), so an
  infostealer that copies the browser profile gets nothing of the wallet. A payment request only fills
  the Send form; you confirm it in the app, where the form and the confirmation say where it came from
  (*your browser (shop.example)*) and that XaultWallet can't confirm who sent it. Its own words are
  cleaned of line breaks, right-to-left overrides and invisible characters.
- **How it connects.** Settings, *Browser companion*, *Connect* writes a native messaging host manifest
  (and on Windows a key under HKEY_CURRENT_USER) that lets only the companion's extension id start
  XaultWallet's own executable as its host; the host passes each checked request to the running app over
  a local channel only your account can open (on Windows, not from the network either), and passes back
  only the fields each reply has. Requests while the vault is locked wait for the unlock, at most 10
  minutes (the lock screen says only that one waits); *Pay* starts the app when it isn't running. A
  request never replaces a form you filled or a payment that may have gone out: it waits behind *Open
  it*. Six payment requests, five new addresses, ten *show* and sixty *status* requests a minute at most.
- **New addresses are announced** across the top of the app's window with their start and end (only
  while the wallet is on screen), and labelled *Browser:* on the Receive tab.
- **Tripwire.** At every start, and when the window comes back to the front, the app checks that each
  browser connection it made still leads to it (the exact path it wrote), and warns across the top of
  the window, naming the program, if one was repointed to another program (the way something could
  stand in for XaultWallet and hand the browser its own address). On Windows it also says so when
  another program took its channel's name first. (A removed connection is reported once; the channel
  and the extension's own files are not covered, see SECURITY.md.)
- **`monero:` links open in XaultWallet** (Windows and Linux), from any browser, once you choose *Open
  them here*. A second launch hands the link to the copy already running.
- **Uninstalling removes them.** The setup and MSI uninstallers run `XaultWallet.exe
  --remove-registrations`, which removes the browser connections and the `monero:` link handler that point
  at that copy (another program's are left alone). Upgrades keep them.
- Releases now carry `XaultWallet-<version>-browser-chrome.zip` and `…-browser-firefox.zip`, built from
  `extension/` with no dependencies. Firefox installs unsigned add-ons only temporarily: until the
  companion is on addons.mozilla.org, load it from `about:debugging` (or use Developer Edition).

### Enhanced Security (Windows, off by default)
- Settings, *Enhanced Security*. While it is on, screenshots, screen recordings, Recall and screen sharing
  show nothing where the window is (the same window flag VeraCrypt uses); only key presses and clicks Windows
  reports as coming from a device act on the app, so input injected by another program and fake window
  messages (how hidden-VNC malware drives a window) are dropped; the window answers no accessibility requests, so
  other programs can't read its text or press its buttons that way; XaultWallet and its monero-wallet-rpc
  get an access list that stops other programs of your account from opening their memory (not every way
  in; see SECURITY.md); and the app refuses to start on a desktop other than the one you see. Details and
  limits in [SECURITY.md](SECURITY.md#enhanced-security).
- Screen readers, voice control, password-manager auto-type and remote-control tools can't operate
  XaultWallet while it is on. Turning it on asks for one click within 20 seconds and turns itself off
  again without it; it is saved as on only after that click, and `--no-enhanced-security` turns it off
  if it later stops accepting your input (the app says so after a few ignored presses). Turning it on
  says which parts Windows refused, if any.
- The published app ignores `DOTNET_STARTUP_HOOKS`, which any program can set for your account.

### Interface
- **The confirmation says which wallet pays** ("From Everyday", with the account when there are several)
  and, for a request from a link or the browser, where it came from. *Review* and *Send now* ignore
  clicks for a moment after a request lands or the confirmation appears.
- **No key press sends a payment.** *Send now* takes a click (mouse, touchpad, touchscreen or pen): it
  never takes the keyboard focus, so neither a stray Enter nor a keystroke another program fakes can
  send. *Review* still works from the keyboard; it only builds the transaction.
- A payment link for another network than the wallet on screen is flagged as soon as it fills the form,
  not at Review.
- Settings warns before *Close* throws away unsaved changes.
- The three boxes of *Prove or check a payment* (Tools) are labelled, so they still say what they hold
  once filled; "24 confirmation(s)" reads "24 confirmations".
- Every message and label in the app is now written without em dashes (the docs already were).

### For integrators of XaultWallet.Core
- Added: the `XaultWallet.Core.Companion` namespace (protocol, framing, endpoint, native host,
  launcher, browser and `monero:` link registration) and `ProcessHardening`, `InputTrust` and
  `UntrustedText` in `XaultWallet.Core.Security`. `MoneroProcessManager` registers its monero-wallet-rpc
  with `ProcessHardening` (a no-op until it is enabled). See [PUBLIC-API.md](PUBLIC-API.md).
- Changed: `MoneroUri.TryParse` now cleans a link's description and recipient name (control, format,
  separator and invisible characters dropped, whitespace collapsed) and shortens them to 200 and 100
  characters.

### Tests
- Core unit tests for the protocol, the browsers' framing, the native host, the local channel (a real pipe
  or socket), registration (a registry in memory on Windows, a throwaway home elsewhere), the input rule
  and the memory protection (a real process whose memory another program can no longer open).
- The extension's own tests (Node's test runner), run in CI with its build.
- The end-to-end runs go through the real native host: on Linux the headless run asks for status, a
  receive address and payments (one while locked) and checks the app's own screens; on Windows the run of
  the published exe also connects both browsers from Settings, opens a `monero:` link through Windows
  itself, checks the tripwire, and checks Enhanced Security from outside (memory closed, no accessibility
  tree, absent from a screen capture, an injected click ignored, no start on a hidden desktop).
- Checked by hand before release: real Chromium and Firefox with the published Linux build as native host.
- An adversarial review of the new code before release, in five parts and a final pass; its fixes are part
  of this version, with tests for each: hidden characters, edge-of-limit sizes, replies rebuilt by kind,
  odd manifests (relative, linked, empty, oversized, a pipe), registry values of the wrong kind, a held
  request over the user's own form, the unconfirmed and command-line ways Enhanced Security is turned
  off, and the uninstallers' cleanup.

## 0.6.0b-beta - 2026-10-11 - security hardening, BSD 3-Clause license

A revision of 0.6.0a (the letter marks it), from an outside security review of the wallet backend,
its session folders and the vault's unlock, and the move to the BSD 3-Clause License. **Your vault,
settings and wallets are unchanged**, with the one behaviour change below that you should know
about: every unlock now rewrites the vault file.

### Behaviour changes to know about
- **Every unlock rewrites the vault.** A duress unlock has to write (it wipes the real slot), and a
  normal unlock used to leave the file alone, so the file's modification time and a short-lived temp
  file told the two apart. Now every successful unlock re-seals the slot it opened (a fresh nonce, no
  extra key derivation) and rewrites the file. The write is best-effort and silent, so a locked or
  read-only vault still opens. The cost, spelled out in [SECURITY.md](SECURITY.md): two copies of the
  vault taken before and after an unlock now show which slot opened, on every unlock rather than only
  on a duress one. A vault from 0.3 has its part re-sealed in 0.5's payload layout at the first
  unlock here (unless that no longer fits its 4 KiB slot, which is then left as it was), so 0.3
  can't open that part afterwards.
- **The wallet backend is checked more often.** Every call that returns secrets or spends (keys,
  signing, proofs, and preparing, relaying or sweeping a send) first re-checks that the backend's
  port is still held by the process the app started, not only when the wallet opens.
- **wallet-rpc: the installed copy comes first.** With no path in Settings, the app now prefers the
  copy it installed, and only while it still matches the checksum recorded when it was installed and
  verified, over one next to the app or on PATH (writable by whoever can write those folders). The
  same check runs before every launch: a copy that changed is refused, with a message to reinstall
  it from Settings. A copy the installer placed also can't be older than the oldest release the
  installer installs. A wallet-rpc you chose yourself is still yours and starts whatever its version.
- **A typed restore height can be refused.** When no node can be asked for its height, a restore
  height that no chain could have reached by now is refused (it would scan nothing and show an empty
  wallet), and any other typed height needs a second press of Add after a message that it couldn't
  be checked.
- **Send starts on the Default priority** (the wallet picks the fee tier), not Low.

### Fixes
- **No wallet-rpc left behind after a crash.** On Linux, with `setpriv` installed (it ships with
  util-linux), the kernel now kills the wallet backend when the app dies, even if it is killed
  outright. Where it isn't, the next start stops a leftover wallet-rpc and shreds its folder.
- **The startup sweep can't be steered.** It, and the shredder, never follow links, and the sweep
  only touches real folders that are yours and closed to others. On Linux, session folders now live
  in your runtime directory (`$XDG_RUNTIME_DIR`, normally held in memory) instead of the shared temp
  folder when that directory is yours.
- Diagnostic output also redacts the camelCase names of the app's own data (`seedOffset`,
  `ephemeralWalletPassword`, `txKey` and the rest).
- The repository pins the .NET SDK band (`global.json`), and the release job checks the NuGet lock
  files before it publishes.

### License and notices
- **XaultWallet is now licensed under the BSD 3-Clause License**, the license Monero's own wallets
  use, instead of MIT. The Settings screen and the installers say so. Releases up to 0.6.0a-beta,
  and a first build of 0.6.0b-beta that was replaced on the day it was published, were MIT, and
  anyone who has them keeps that license.
- **[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) now travels with the executable** (in the zip, the
  tarballs and both installers). It credits the Monero code behind the restore-height estimate and
  reproduces the notices of the .NET runtime, the libraries and the Inter font built into the app.

### For integrators of XaultWallet.Core
- The license is BSD 3-Clause. No signatures changed. `VaultManager.Unlock` and `OpenSession` now rewrite the vault on every
  successful unlock (see above). New: `WalletRpcInstaller.FindInstalledVerified` and
  `EnsureMatchesManifest`, and `RestoreHeights.IsBeyondPlausibleTip`.
  `MoneroDiagnostics.ProbeWalletRpcAsync` refuses an installer-placed copy older than the minimum,
  and the secret-bearing calls of `MoneroWalletService` throw if the backend is no longer the app's
  own child.

### Not in this release
- The OpenPGP verifier still doesn't read a signature's creation time; [SECURITY.md](SECURITY.md)
  says what the installers trust today.

## 0.6.0a-beta - 2026-10-08 - Avalonia 12

A revision of 0.6.0 (the letter marks it): the UI framework moves to Avalonia 12, and nothing else
about how the wallet works changes. **Your vault and settings are unchanged**; 0.6.0 and 0.6.0a open
the same files.

### Under the hood
- **Avalonia 12.1.3** (from 11.1.3), with SkiaSharp 3 and HarfBuzz 8 for drawing and text. Every
  screen renders as before: the headless UI checks, the screenshot run and the Windows end-to-end
  test of the released exe all pass on it.
- **The title bar** is now marked as one for the system, so dragging, double-click to maximise and
  snapping are the operating system's own rather than the app's imitation of them. The window keeps
  its own look and buttons. The Windows end-to-end test now checks it with the real mouse: a drag
  moves the window, a double-click maximises and restores it, and its buttons still take clicks.
- **Copying an address or key** works as before, Windows clipboard-history exclusion included,
  rewritten for Avalonia 12's clipboard API. The Windows end-to-end test now reads the clipboard
  back after a copy: the address, marked to stay out of clipboard history and the cloud clipboard.
- **The screenshot tool and the in-process end-to-end test** keep the vault, settings and downloads
  in a throwaway folder on Windows too. They redirected HOME and APPDATA, which Windows ignores for
  these folders, so on a developer's machine they read that person's settings and saved Hide amounts
  back. The app now takes the throwaway folder directly, and both refuse to run if any folder lands
  outside it. The README screenshots are re-rendered from that clean profile.

### Dependencies
- Two packages the app no longer used are gone: Avalonia.Controls.DataGrid (History stopped using a
  grid in 0.5) and Avalonia.Diagnostics (developer tools, never wired up). The downloads still grow
  by 1 to 2 MB with Avalonia 12 and its newer Skia and HarfBuzz.
- Tmds.DBus.Protocol no longer needs a pin: Avalonia 12 brings 0.94.1, which has the fix for
  GHSA-xrw6-gwf8-vvr9.
- With no package depending on the build configuration any more, CI now checks Release builds
  against the committed lock files too, not only Debug ones.
- CI builds opt out of Avalonia's build-time usage statistics, as they already did for .NET's.
- Replaces Dependabot's five separate Avalonia bumps (#13, #15 to #18), which could not pass on
  their own: each moved one package to 12 and left the rest on 11. Dependabot now proposes
  Avalonia's packages together, in one pull request.

## 0.6.0-beta - 2026-10-08 - built-in Tor, coin control, Windows installers

**Your vault keeps its format**: 0.5 vaults open as they are. 0.5 can still open a vault after 0.6
has saved it (it ignores the new frozen-coins field). Screenshots of everything new are in the
[README](README.md#screenshot-tour).

### Built-in Tor
- **XaultWallet can run its own Tor**, Wasabi-style. Settings, Network & privacy, now asks how the
  app reaches the network: **Tor, built in** (recommended), **my own SOCKS proxy**, or **direct**.
- **Download & install Tor** fetches the Tor Expert Bundle from torproject.org and installs it only
  after Tor Project's signature on the release's checksum list verifies against the Tor Browser
  Developers signing key built into the app, and the download matches the signed checksum. Older
  releases than the one installed, or than the app's minimum, are refused. Or point Settings at a
  tor you installed yourself. Details: [SECURITY.md](SECURITY.md#the-tor-installer).
- Tor starts with the app and stops with it. Its progress, and **Start / restart** and **Stop**,
  are in Settings.
- **It fails closed.** While Tor is chosen, every wallet's node traffic, the app's own node checks
  and the monero-wallet-rpc download go through it, and while it connects, nothing goes out
  directly: wallets say "Connecting to Tor" and wait. A node on this computer is reached directly
  (Tor can't reach loopback, and nothing leaves the machine).
- **A route badge** on the wallet screen says how it reaches its node: TOR, PROXY, DIRECT or
  LOCAL NODE, with TOR · RECONNECTING if Tor stops.
- If a firewall blocks tor, Settings says so (and which file to allow) within seconds instead of
  waiting out a timeout.

### Coin control
- **A Coins tab** lists the account's unspent coins: amount, the address label it arrived at (who
  knows about it), confirmations, and whether it is still maturing.
- **Freeze** a coin and no transaction spends it, Send max included, until you unfreeze it. The
  balance card shows how much is frozen, and the send confirmation says how many frozen coins were
  left out.
- Frozen coins stay frozen after a lock: the list is kept in the vault and applied again as soon as
  the wallet finds the coins, and again right before every send. Details:
  [SECURITY.md](SECURITY.md#coin-control).

### Quality of life
- **Keyboard shortcuts**: Ctrl+1 to Ctrl+6 for the tabs, Ctrl+R refresh, Ctrl+L lock, Ctrl+H hide
  amounts (Cmd on macOS). The tooltips say so.
- **Time left while syncing**: the sync line estimates how long until the wallet catches up.
- **The window opens at the size you left it** (maximised too).

### Windows installers
- Releases now include **`XaultWallet-<version>-win-x64-setup.exe`**: installs for the current user
  without administrator rights (or for everyone, if chosen), adds a Start menu entry and an
  uninstaller, and never touches your vault.
- And **`XaultWallet-<version>-win-x64.msi`**, the same as a Windows Installer package (per user, no
  administrator rights), for those who deploy MSIs. Pick one of the two: both install into
  `%LOCALAPPDATA%\Programs\XaultWallet`.
- Both are unsigned, like the zip, which is still there. The release workflow test-installs and
  uninstalls each before anything is published.

### For integrators of XaultWallet.Core
- No breaking changes. New: the `XaultWallet.Core.Tor` namespace (`TorInstaller`, `TorProcess`,
  `TorDiagnostics`, `TorReleaseList`, `TorSigningKeys`), coin control on `MoneroWalletService`
  (`GetCoinsAsync`, `SetFrozenAsync`, `ApplyFrozenAsync`) and `WalletSecrets.FrozenKeyImages`, sealed as
  `frozen` in every slot. See [PUBLIC-API.md](PUBLIC-API.md).

## 0.5.1-beta - 2026-10-07 - hardening fixes, .NET 10

A security and hygiene release from a code review of 0.5.0-beta. **Vault format and data are
unchanged**: 0.5.0 vaults open as they are.

### Behaviour changes to know about
- **https:// nodes now require a certificate that monero-wallet-rpc can verify.** Before, an https
  node with a bad certificate was accepted with only a log warning, and a failed TLS handshake fell
  back to plaintext. Now the app starts wallet-rpc with `--daemon-ssl enabled` for https:// nodes
  (and sets the same on a node switch), so a self-signed node no longer connects. http:// nodes
  behave as before. See "Choosing a daemon" in [SECURITY.md](SECURITY.md).
- **macOS fails closed.** The check that the wallet-rpc port belongs to the process the app started
  does not exist on macOS, and the app used to carry on without it. It now refuses to send seeds
  there. The osx-arm64 build still builds, but seed-bearing use on macOS does not work until a real
  port-ownership check lands. Windows and Linux are unchanged.
- **Seed imports need monero-wallet-rpc.** Importing a seed without it installed used to skip the
  address confirmation and seal the wallet anyway, so a wrong seed or seed offset opened a different
  empty wallet later with no error. It is now refused with a message to install wallet-rpc first.

### Fixes
- **Backup file names no longer reveal a decoy.** The save dialog suggested different names for the
  real and the decoy seed backup. Both now get the same neutral name, `wallet-backup-<date>-<digits>.txt`.
- **Download & install refuses downgrades.** It will not replace a newer wallet-rpc with an older
  signed release, nor install one older than the version the app expects (0.18.5.1). Nothing is
  downloaded in either case.
- **Copied secrets leave the clipboard sooner.** Locking or closing the app clears an address or key
  the app copied, as long as the clipboard still holds it. On Windows, copies are excluded from
  clipboard history and the cloud clipboard.
- **A cancelled sweep says so.** Locking or closing during the first relay of a send-max sweep now
  reports "check History" instead of looking like a clean cancel, since the request may have reached
  the network.
- **Seed-verification quiz positions use a cryptographic RNG.** No security impact; it matches the
  rest of the code.
- SECURITY.md now names the string binding of the unlock password and the unzeroed copy that the
  Argon2 library keeps, in the memory-forensics notes.

### Build and supply chain
- **.NET 10.** The whole solution targets .NET 10 (8 reaches end of support on 2026-11-10).
  Avalonia stays at 11.1.3. `Tmds.DBus.Protocol` is pinned to 0.21.3 (GHSA-xrw6-gwf8-vvr9).
- **Pinned and locked.** GitHub Actions are pinned to full commit SHAs, Dependabot keeps them and
  the NuGet packages current, and `packages.lock.json` is committed for every project. CI restores in
  locked mode, so a different package graph than the committed one fails the build.
- The release workflow is read-only by default; only the job that drafts the release can write.

## 0.5.0-beta - 2026-10-06 - several wallets, contacts, payment requests, verified backend install

Everything below is new since 0.3.0-beta (there was no 0.4). **Your vault keeps its format** (about
4 KiB per password) until you upgrade it in Settings → *Vault format*; 0.3 can't open it after that,
nor a password's part once 0.5 has saved it. If you have a duress password, read
[Upgrading to 0.5](SECURITY.md#upgrading-to-05-vault-format-2) before you unlock with your main one,
and export a backup if you might go back. Audit notes:
[docs/AUDIT-2026-10.md → Fourth pass](docs/AUDIT-2026-10.md#fourth-pass-050).

### Several wallets behind one password
- **Add as many wallets as you like** to the open vault: new (seed generated by monero-wallet-rpc,
  written down and checked), restored from a seed, restored from keys (address + view and spend
  keys), or **watch-only** (address + view key). Imports are opened by monero-wallet-rpc before
  anything is saved (keys that don't belong to the address are refused) and you confirm the address.
- **Switch** from the wallet's name at the top. A wallet starts when you choose it (the arrow keys open
  the list rather than step through it), then keeps running and syncing (once a minute in the
  background) until you lock; the vault reopens on the wallet you used last.
- **Manage** each wallet: rename it, change its node (applied at once, no rescan, no password), create
  and name **accounts**, show its **seed and keys** (after your password), or remove it (password and
  its name; never the last one).
- The duress password opens its **own** set of wallets and contacts, which can have several wallets
  too. Nothing in either mentions the other, and both slots stay the same size whatever they hold.

### For paying and getting paid
- **Contacts**: an address book shared by the wallets of a password; Send shows who you're paying,
  History who you paid, and *Pay* fills the send form. After paying a new address, save it in one click.
- **Several recipients in one transaction** (up to 15, one fee). The confirmation lists each by name
  and amount, built into the signed transaction before you confirm.
- **Payment requests**: an amount and a description go into the QR code and a standard `monero:` link.
  **Paste a `monero:` link** into Send and the form fills itself, showing what it's for. Links with a
  payment ID or several addresses, and amounts like `1,5`, are refused rather than guessed.
- **Subaddresses that are remembered**: label them, see which have been paid, show any one's QR code.
  How many you've handed out is saved in the vault, so after a lock the next one is new (before, the
  numbering restarted every unlock and the same subaddress could be handed to two people).
- **History**: filter (received / sent / pending), search (notes, txids, names, amounts), and details
  for each transaction: confirmations, which address received it, where it went, the fee, and **a note**
  only you see. *Prove this payment* fetches its transaction key. CSV export includes the notes.
- **Tools**: prove or check a payment; **sign a message** with your address and check others'; **prove
  your balance** with a reserve proof and check others'; re-check spent outputs.

### monero-wallet-rpc: bring your own, or one click, verified
- Bringing your own remains the recommendation. Otherwise **Download & install** (on the startup
  screen, which now stops and offers it when no binary is found, and in Settings) fetches the
  official CLI from getmonero.org, checks `hashes.txt` against **binaryFate's OpenPGP signature** (his
  key ships in the app and is accepted only for its pinned fingerprints) and the archive against its
  signed SHA-256, then installs only `monero-wallet-rpc` in your user profile. Any failed check
  installs nothing. Proven for real: the Windows end-to-end test now starts without a binary and
  installs it this way, from getmonero.org.

### Security
- **Vault format 2**: each password's profile (wallets, contacts, labels, notes) is sealed in a 256 KiB
  slot, so the file size says nothing about what it holds. Payload v3 has the same fields for every
  slot. New vaults use it; a vault from 0.2/0.3 converts only when you ask (Settings → *Vault format*),
  because converting re-seals just the slot you unlocked: the other is carried over byte for byte until
  its own password opens it, and until then someone holding that password could tell. The upgrade
  says so and suggests the order that avoids it
  ([details](SECURITY.md#upgrading-to-05-vault-format-2)).
- **A duress wipe that couldn't be written is not forgotten**: the next write to the vault, by either
  password, applies it first.
- **A vault replaced while open is never written into**: the session checks the file still holds its
  slot before saving, and refuses (lock and unlock again) otherwise.
- **An open vault saves through its session**: changes re-seal only the open profile's slot with the
  key derived at unlock (the other slot is written back untouched), and changing the password from an
  open wallet changes that profile's only. Saves are taken on the UI thread and finished before a lock
  completes, so a lock never interrupts a write. Locking takes the wallets off the screen at once; the
  vault opens again only after the lock has finished.
- **Restoring from keys is checked.** monero-wallet-rpc's `generate_from_keys` accepts a view key that
  doesn't belong to the address, which makes a wallet that silently never sees a payment. The app
  reopens the wallet, where Monero's own key check runs, and refuses the keys, saying which one is wrong.
- **Re-checking spent outputs needs your own node.** It sends every key image the wallet has to the
  node, so it is offered only with a node on this computer.

### Changed
- The wallet screen: the wallet's name (and switcher) heads the balance card; tabs are Receive, Send,
  History, Contacts and Tools; *Verify a payment* moved from Send to Tools.
- *Change node* moved from Settings to each wallet's **Manage** sheet; Settings keeps the vault password.
- GitHub Actions moved off the deprecated Node 20 runtime.

### For integrators of XaultWallet.Core
- **Breaking:** `VaultManager.Unlock` returns a `WalletProfile` (`UnlockResult.Profile`;
  `UnlockResult.Secrets` is its active wallet); `VaultManager.ChangeDaemonAddress` is gone: open a
  `VaultSession` (`VaultManager.OpenSession`) and `Save()` it. `WalletSecrets` gained `Id`, `Name`,
  `Kind` (seed / keys / watch-only), `Address`, `ViewKey`, `SpendKey` and per-wallet labels, counts and
  notes. New: `VaultSession` (with `IsOldFormat` and `UpgradeFormat`), `WalletProfile`, `Contact`,
  `MoneroUri`, the `Installer` namespace, and accounts, subaddress, signing, reserve-proof and
  multi-destination calls on `MoneroWalletService`. `VaultFullException` says whether an upgrade would
  make room (`OldFormat`). See [PUBLIC-API.md](PUBLIC-API.md).

## 0.3.0-beta - 2026-10-04 - tested end to end, motion & polish

Everything below is new since 0.2.0-beta. **Coming from 0.1?** The vault format changed in 0.2.0-beta:
read its [notes](CHANGELOG.md#020-beta---2026-10-03---security-audit--redesign) and
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
  broadcast: check History before sending again*, or not sent, each clearly coloured.
- Copy and export feedback is a toast at the bottom, on whichever tab you're on.
- Unlock says *Unlocking…* while your key is derived.

### For integrators of XaultWallet.Core
- No breaking changes. New: `DaemonAddress.IsLoopback`, `MoneroDiagnostics.IsLocalTestChainAsync`,
  `MoneroProcessManager.IsLocalTestChain`, `MoneroWalletService.IsLocalTestChain`. See
  [PUBLIC-API.md](PUBLIC-API.md).

## 0.2.0-beta - 2026-10-03 - security audit & redesign

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

## 0.1.0-beta - 2026-07-09 - proof of concept

Initial public proof of concept.
