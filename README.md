# XaultWallet

**A privacy-first desktop Monero (XMR) wallet with a duress password that opens a decoy wallet.**
Built on .NET 10 + Avalonia. Encrypted at rest with AES-256-GCM (Argon2id KDF). Drives the official
`monero-wallet-rpc` — it never reimplements Monero's cryptography. Its own Tor (verified when it is
installed, and fail-closed while it runs) and coin control come built in.

![License: MIT](https://img.shields.io/badge/license-MIT-green)
![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4)
![Avalonia 11](https://img.shields.io/badge/Avalonia-11-8B44AC)
![Status](https://img.shields.io/badge/status-unaudited%20beta-red)
![Platforms](https://img.shields.io/badge/release-Windows%20%7C%20Linux%20%7C%20macOS-blue)

<p align="center">
  <img src="docs/screenshots/wallet-receive.png" width="49%" alt="Wallet: receive with QR code, routed through the built-in Tor" />
  <img src="docs/screenshots/wallet-coins.png" width="49%" alt="Wallet: the Coins tab, with one coin frozen" />
</p>

## Notification - Documentation and code review assisted by Claude Code.

## What's new in 0.6

- **Built-in Tor.** Choose *Tor, built in* in Settings and XaultWallet runs its own Tor: installed
  from torproject.org only after Tor Project's signature and the download's checksum verify, started
  with the app, and the only way node traffic may leave while it is on. Nothing goes out directly
  while Tor connects. [How-to #10](#10-route-everything-through-tor)
- **Coin control.** A Coins tab lists every unspent coin with the label of the address it arrived
  at. Freeze the ones you don't want to spend, so a payment can't combine them with others; frozen
  stays frozen across locks. [How-to #11](#11-coin-control-freeze-coins)
- **Windows installers**: `…-win-x64-setup.exe` or `…-win-x64.msi`, per-user, no administrator
  rights, Start menu entry and uninstaller, never touching your vault.
- **Keyboard shortcuts** (Ctrl+1 to 6, R, L, H), a time-left estimate while syncing, and a window that
  reopens at the size you left it. [Shortcuts](#12-keyboard-shortcuts)

<p align="center">
  <img src="docs/screenshots/settings-tor.png" width="49%" alt="Settings: the built-in Tor, connected" />
  <img src="docs/screenshots/wallet-tor-connecting.png" width="49%" alt="A wallet waiting for Tor: TOR · RECONNECTING, nothing sent directly" />
</p>

---

## Contents

- [What's new in 0.6](#whats-new-in-06)
- [Highlights](#highlights)
- [How it works (trust model)](#how-it-works-trust-model)
- [Quick start (~10 minutes)](#quick-start-10-minutes)
- [How-to guides](#how-to-guides)
- [Screenshot tour](#screenshot-tour)
- [What syncs from where (restore heights)](#what-syncs-from-where-restore-heights)
- [Where your data lives](#where-your-data-lives)
- [Security model in one page](#security-model-in-one-page)
- [Architecture](#architecture)
- [Build from source](#build-from-source)
- [Troubleshooting](#troubleshooting)
- [FAQ](#faq)
- [Roadmap](#roadmap)
- [Contributing, license, acknowledgements](#contributing-license-acknowledgements)

---

## Highlights

| | |
|---|---|
| **Encrypted vault** | Your seeds are sealed with **AES-256-GCM**, key derived by **Argon2id** (256 MiB, 4 iterations). The only file that persists is `vault.xv`. |
| **Several wallets, one password** | Keep as many wallets as you like behind one password and switch between them from the top of the screen: new ones, restored from a seed or from keys, or **watch-only** (address + view key). Each runs its own backend and keeps syncing in the background. |
| **Duress password** | A second password opens a **decoy profile** that looks completely normal. Both slots are equal-size whatever they hold (256 KiB each in 0.5's vault format) and position-randomised, and **their decrypted contents have exactly the same shape** — even someone holding the vault file *and* the duress password finds no marker of a second profile. Optional: using the decoy can **wipe** the real profile from the device. |
| **Contacts, labels, notes** | An address book, names for your subaddresses and accounts, and a note on any transaction — all sealed in the vault with the wallets (never in wallet files, which are shredded on lock). |
| **Pay several people at once** | Up to 15 recipients in one transaction, one fee. Paste a `monero:` payment link and the form fills itself; ask for a payment with your own link and QR code (amount and description included). |
| **Exact fee before you send** | The transaction is built and signed first (unbroadcast); the confirmation shows the **exact fee and total**. Confirm broadcasts that same signed transaction. |
| **Amounts you can trust** | Typed amounts are parsed the same way on every system — `0,25` is 0.25 XMR everywhere, never 25 — and read back to you before anything is built. |
| **Receive with QR** | Your address as a QR code (`monero:` URI) next to the full address in a monospace face. CI decodes the rendered QR and checks it matches the address shown. |
| **Address echo on import** | Importing a seed shows the **derived primary address** to confirm before anything is saved — catching a wrong seed-offset, typo, or network. |
| **Proofs** | The **transaction key** of any payment you sent (safe to share — it cannot spend) and a check for anyone's payment; **signed messages** (prove you own an address) and **reserve proofs** (prove a balance) — make and check both. |
| **Verified one-click backend** | Bring your own `monero-wallet-rpc` (recommended), or press **Download & install**: the app checks getmonero.org's `hashes.txt` against **binaryFate's signature** (key pinned in the app) and the download against its signed SHA-256 before installing anything. |
| **Built-in Tor** | Choose *Tor, built in* and the app runs its own `tor`: node traffic, node checks and downloads go through it, and **nothing goes out directly while it connects** (fail closed). *Download & install Tor* checks Tor Project's signature (Tor Browser Developers key, pinned in the app) and the download's signed SHA-256 first. A badge on every wallet says how it reaches its node. |
| **Coin control** | The **Coins** tab lists unspent coins with the address label they arrived at. **Freeze** one and no transaction spends it (Send max included) until you unfreeze it; the frozen list is sealed in the vault and survives a lock. |
| **Windows installers** | A per-user `setup.exe` or `.msi` (no admin rights), Start menu entry and uninstaller; the vault in `%APPDATA%` is never touched. Or keep using the zip. |
| **Locked-down backend** | wallet-rpc runs on loopback with **per-session random credentials**, its files (wallet, log, ring database) live in one folder **shredded on lock**, and the app checks the port belongs to the process it started before sending it anything. |
| **Hygiene by default** | Copied addresses/keys **auto-clear from the clipboard after 30 s**. Logs **redact seeds, passwords and keys** and record nothing that tells your two wallets apart. Auto-lock on inactivity. |
| **No hand-rolled crypto** | All key derivation, signing, proofs and address logic is done by the **official `monero-wallet-rpc`**. |

## How it works (trust model)

The most dangerous thing a wallet author can do is reimplement Monero's cryptography. XaultWallet
deliberately does not. It launches the **official `monero-wallet-rpc`** binary (which you download
from [getmonero.org](https://www.getmonero.org/downloads/) and verify yourself, or let the app fetch
and verify — see [Step 2](#step-2--get-monero-wallet-rpc)) as a **loopback-only,
password-protected child process** on a random local port, restores your wallet from seed into a
**private temporary folder**, and talks JSON-RPC to it. That child syncs against a `monerod` node —
yours, or a public one.

```
┌─────────────────┐  JSON-RPC (127.0.0.1, random port,   ┌────────────────────┐  built-in Tor   ┌─────────┐
│   XaultWallet    │ ───── per-session digest auth) ─────▶│  monero-wallet-rpc  │ ── (or your ──▶ │ monerod │
│  (this project)  │                                       │  (official, yours)  │   own proxy)    │ (a node)│
└────────┬────────┘                                       └────────────────────┘                 └─────────┘
         │ owns: encrypted vault (seed at rest), duress logic, UI
         │ never: keys, signing, address derivation — that's Monero's official code
```

XaultWallet's own code is responsible for exactly three things: the **encrypted vault format**, the
**duress/deniability logic**, and the **UI**. With built-in Tor on, the link to the node goes
through a `tor` the app runs (Tor Project's own build, signature-checked when it was installed):
the node never sees your IP address.

## Quick start (~10 minutes)

**You need three things:** this release, the official Monero CLI tools, and a node to sync against.

### Step 1 — Get the release

Download the latest `XaultWallet-<version>-<platform>` archive from the releases page, check it
against `SHA256SUMS`, and extract it. It is one self-contained executable — **no .NET install
required** (Windows 10/11 x64, Linux x64, macOS Apple Silicon). On Windows you can run
`XaultWallet-<version>-win-x64-setup.exe` (or the `.msi`, if you deploy MSIs) instead: it installs
for your user without administrator rights, adds a Start menu entry and an uninstaller, and never
touches your vault. Binaries and installers are currently **unsigned**, so Windows SmartScreen /
macOS Gatekeeper will ask before the first run.

### Step 2 — Get monero-wallet-rpc

This wallet's whole trust model rests on that binary being genuine. Two ways:

- **Recommended — your own, verified by you.** Download the **Monero CLI** (not GUI) from
  <https://www.getmonero.org/downloads/>, verify it (import the signing key, check your archive's hash
  against the signed `hashes.txt`; details: [SETUP-MONERO-RPC.md](SETUP-MONERO-RPC.md)), extract it,
  and note the full path of **`monero-wallet-rpc`** — or put it on your PATH.
- **Convenient — let the app do it.** On first launch, if no `monero-wallet-rpc` is found, the startup
  screen offers **Download & install** (also in Settings → *Wallet backend*). It downloads the official
  CLI for your system from getmonero.org, checks `hashes.txt` against **binaryFate's signature** (his
  key ships inside XaultWallet, pinned by fingerprint) and the archive against its **signed SHA-256**,
  then installs only `monero-wallet-rpc` in your user profile. If any check fails, nothing is
  installed. What this trusts, exactly: [SECURITY.md](SECURITY.md#the-monero-wallet-rpc-installer).

<p align="center"><img src="docs/screenshots/startup-setup.png" width="60%" alt="Startup: monero-wallet-rpc not found — Download &amp; install, or choose your own copy" /></p>

### Step 3 — First launch & settings

1. Run XaultWallet. The splash checks for the binary and a node. Without a binary it stops and offers
   *Download & install* or *I have it: choose my copy…*; without a reachable node it offers
   **Continue anyway** (expected on a fresh machine).
2. To use your own copy: **Settings** (top-right) → **Wallet backend**: *Browse…* to
   `monero-wallet-rpc` (or leave it blank if it's on your PATH — the resolved path is shown) → **Test**.
   A typed path must be a full path; a bare `monero-wallet-rpc` is looked up on PATH, and relative
   paths are refused.
3. **Network & privacy**: pick a **stagenet** public node from the list (or your own `monerod
   --stagenet`) → **Test**. Under *How XaultWallet reaches the network*, choose **Tor, built in**
   and *Download & install Tor* if you want nodes never to see your IP address
   ([How-to #10](#10-route-everything-through-tor)). **Save changes**, then **Close**.

### Step 4 — Create a wallet on stagenet

Follow [How-to #1](#1-create-your-first-wallet). Stagenet coins are worthless by design — exactly
what you want while learning.

### Step 5 — Get stagenet coins and play

Grab coins from a community faucet (search "monero stagenet faucet"), then exercise the full loop:
receive, watch it confirm, send some back, lock, unlock. Walkthrough: [STAGENET-TESTING.md](STAGENET-TESTING.md).

---

## How-to guides

### 1) Create your first wallet

<img src="docs/screenshots/create-vault-seed.png" width="60%" align="right" alt="Writing down the recovery seed" />

1. **Network & node** — keep **Stagenet**, enter a node or pick a preset.
2. **Recovery seed** — keep *Create a new wallet* and click **Generate my seed**. The seed is generated
   by `monero-wallet-rpc` itself. **Write the 25 words down, in order, on paper**, then either
   **verify** three of them or **save a backup file** (plaintext — store it offline).
3. **Vault password** — the meter refuses passwords that are trivially guessable (common words,
   sequences, repeats). A long passphrase of unrelated words is both strong and memorable.
4. (Optional) **Duress password** — see [How-to #3](#3-set-up-the-duress-decoy-password).
5. **Create vault**, unlock, and the wallet opens while it scans in the background.

A freshly generated wallet scans only from the moment it was created (minus a day's safety margin) —
there is nothing older to find, so its first sync is fast.

<br clear="right"/>

### 2) Import an existing wallet (with optional seed-offset passphrase)

1. Under **Recovery seed**, choose *Import an existing seed* and paste your 25 words.
2. **Seed offset passphrase** — leave **blank** unless the wallet was created elsewhere *with* one.
   It is case- and space-sensitive, a **separate secret** the 25 words cannot recover, and a wrong or
   missing offset silently opens a different, **empty** wallet.
3. **Scan from** — *Full history* (default, safest), *a specific block height* (your wallet's
   creation height if you know it; `3,150,000` and `3.150.000` both work), or *Now* (only for a
   brand-new seed with no history).
4. Set your password(s) and **Create vault**.
5. **Is this your wallet?** — XaultWallet opens the seed once and shows the **address it derives**.
   Compare it with the wallet you meant to import. If it doesn't match, go back and fix it — nothing
   is saved until you confirm.

### 3) Set up the duress (decoy) password

The duress password opens a second, fully functional wallet that looks identical in the app. Under
coercion you type the duress password instead of the real one.

1. On the create screen, switch on **Duress password**.
2. Choose a duress password (different from the main one; it should look like a real password).
3. Give the decoy a seed: **Generate a decoy** (fresh) or **Import a decoy seed** (one you control
   with a plausible small balance — an imported decoy always scans full history).
4. Save the decoy's backup too, and consider keeping a little XMR on it — an empty decoy is less convincing.
5. **Wipe the real wallet when the duress password is used** — read twice before ticking. Any use of
   the duress password (unlocking, changing its password or node) overwrites the real wallet's slot
   with random data **permanently, on that device**, and destroys vault copies kept by Settings →
   Restore. Only enable it if your real seed is backed up elsewhere, and never use the duress
   password yourself.

**Deniability, honestly stated:** the vault always contains two equal-size slots; without a duress
password the second slot is random filler, cryptographically indistinguishable from an encrypted
profile. Both slots' decrypted contents have the same fields — there is no "real"/"decoy" marker,
label, or password list anywhere. Each password's profile has its own wallets and contacts: the
decoy can hold several wallets too, and nothing in it mentions the other. Every action in the app
behaves the same for both. The limits — what an examiner *can* learn — are spelled out in
[SECURITY.md](SECURITY.md).

### 4) Receive and send

**Receive** — the Receive tab shows an address as text and as a QR code; **Copy address** puts it on
the clipboard (auto-clears after 30 s). **New subaddress** (with an optional label, e.g. "Alice")
gives a fresh, unlinkable address for the same wallet — good practice is a new one per payer. The
**Addresses** list shows them all with their labels, marks the ones that have been paid (*used*), and
shows any one's QR code. To **request a payment**, type an amount and what it's for: the QR code and
**Copy payment link** then carry a standard `monero:` link that wallets fill in for the payer.
Subaddresses you've handed out are remembered in the vault, so after a lock the next one is new.

<p align="center"><img src="docs/screenshots/wallet-receive-addresses.png" width="60%" alt="Receive: labelled subaddresses, used ones marked" /></p>

**Send** —
1. Paste the destination address — or a `monero:` payment link, which fills in the amount and shows
   what it's for — or pick someone from **Contacts**, and type the amount. The line under the amount
   shows exactly how it was read (e.g. `= 0.25 XMR`). **Add another recipient** to pay several people
   in one transaction (up to 15, one fee). Pick a priority and click **Review & send**.
2. The transaction is **built and signed but not broadcast**. The confirmation shows every recipient
   (by contact name when it is one), the amount, **exact network fee** and **total** — locked into the
   signed transaction.
3. **Send now** broadcasts exactly that transaction. **Cancel** discards it; nothing touched the network.
4. After a send, the **payment proof** panel shows the txid and transaction key, and a new recipient
   can be saved as a contact in one click.

**Send max** sweeps the entire spendable balance through the same review → confirm flow.

If a broadcast fails or is interrupted, the message includes the txid and tells you to check History
before retrying — retrying a transaction that actually went through would pay **twice**.

<p align="center">
  <img src="docs/screenshots/wallet-send-multi.png" width="49%" alt="Send: a payment link pasted, a second recipient from Contacts" />
  <img src="docs/screenshots/wallet-send-confirm-multi.png" width="49%" alt="Confirmation of a two-recipient send with the exact fee" />
</p>

### 5) Prove a payment, sign a message, prove a balance (Tools)

<img src="docs/screenshots/wallet-tools.png" width="50%" align="right" alt="Tools: prove or check a payment, sign and check messages" />

To **prove** your payment, share the **transaction ID** and **transaction key** (shown after every
send; for an older one, open it in History and choose *Prove this payment*, or use *Fetch key from
this wallet* in Tools). The tx key proves that one payment and **cannot spend anything**. Never share
your seed or spend key. To **check** someone's payment, enter their txid, tx key and the address that
was paid: the wallet reports exactly how much that address received and how many confirmations it has.

Also in **Tools**:
- **Sign a message** with your main address (proves you control it, moves nothing), and **check**
  someone else's signed message.
- **Prove your balance** with a reserve proof — at least an amount, or the whole account — and check
  someone else's. Whoever checks it learns the total proven.
- **Re-check spent outputs**, if a misbehaving node left your balance wrong.

<br clear="right"/>

### 6) Several wallets: add, switch, manage

<p align="center">
  <img src="docs/screenshots/add-wallet.png" width="49%" alt="Add a wallet: new, from seed, from keys, or watch-only" />
  <img src="docs/screenshots/wallet-manage.png" width="49%" alt="Manage a wallet: name, node, accounts, backup, remove" />
</p>

- **Add** — the **+** next to the wallet's name: a **new wallet** (seed generated by
  `monero-wallet-rpc`, written down and checked like the first), **restore from seed**, **restore from
  keys** (address + private view and spend keys), or **watch-only** (address + private view key: sees
  incoming payments, can't spend, and can't see what was spent, so its balance reads as *received*).
  Before an import is saved, `monero-wallet-rpc` opens it — refusing keys that don't belong to the
  address — and you confirm the address it opens.
- **Switch** — pick a wallet from the list under its name. Each wallet you open keeps running (and
  syncing, once a minute) until you lock, so switching back is instant. The vault reopens on the
  wallet you used last. **Lock** locks them all.
- **Manage** — rename the wallet; change its **node** (applies at once, no rescan); create and name
  **accounts** (separate balances inside one wallet; pick one next to the balance); show its **seed and
  keys** (after your password); or **remove** it from the vault (your password and its name; the last
  wallet can't be removed — its money stays on the blockchain, reachable only with the seed).

### 7) Contacts, history, notes

<p align="center">
  <img src="docs/screenshots/wallet-contacts.png" width="49%" alt="Contacts" />
  <img src="docs/screenshots/wallet-history-details.png" width="49%" alt="A transaction's details and note in History" />
</p>

- **Contacts** — save the addresses you pay: Send shows who you're paying, History who you paid, and
  *Pay* fills the send form. Contacts belong to the password's profile: every wallet in it shares them.
- **History** — filter (received / sent / pending), search (notes, txids, names, amounts), and click a
  transaction for its details: confirmations, which of your addresses received it, where it went, the
  fee, and **a note** only you see (sealed in the vault). **Export CSV** includes the notes.

### 8) Change a password

Settings → **Vault password**. With a wallet open, it changes the password of the profile that's open —
only that one, whichever it is. From the unlock screen, it changes whichever profile the current
password opens. Your seeds don't change. It works the same for the main and the duress password (an
asymmetric rule would reveal which is which). A new password that would also open the *other* profile
is refused.

### 9) Coming from 0.3

<img src="docs/screenshots/settings-vault-format.png" width="50%" align="right" alt="Settings: the vault format card, asking before it upgrades" />

Your vault opens in 0.5 as it is and keeps 0.3's format — about 4 KB per password, enough for a
wallet or two with some labels and contacts — until you upgrade it in Settings → **Vault format**
(256 KB per password, the size of every vault 0.5 creates). Before you start:

1. **Export a backup** (Settings → *Export backup*) if you might go back to 0.3. 0.3 can't open an
   upgraded vault, nor a password's part once 0.5 has saved it.
2. **If you have a duress password** (without wipe-on-duress), unlock with it first and wait until its
   wallet is ready; upgrade from there if you want the room. Then use your main password as usual.
   Until a password has been used in 0.5, someone holding it and the vault file could tell that the
   other one was — [Upgrading to 0.5](SECURITY.md#upgrading-to-05-vault-format-2) explains why, and
   what to do if wipe-on-duress is on.

<br clear="right"/>

### 10) Route everything through Tor

<img src="docs/screenshots/settings-tor-connecting.png" width="50%" align="right" alt="Settings: the built-in Tor connecting, 45%" />

1. **Settings** → **Network & privacy** → *How XaultWallet reaches the network*: choose
   **Tor, built in**.
2. **Download & install Tor**. XaultWallet asks torproject.org for the current release, checks Tor
   Project's signature on its checksum list against the Tor Browser Developers key built into the
   app, checks the download against the signed checksum, and installs only `tor` in your user
   profile. If any check fails, nothing is installed. (Already have tor? Put its path in the box
   instead, and **Test**.)
3. **Save changes**. Tor starts (the status line shows its progress) and from then on starts with the
   app. Wallets you open now reach their node through it: the badge next to the wallet's name says
   **TOR**.

While Tor is chosen, **nothing goes out directly**: if Tor is still connecting, or stopped, a wallet
waits ("Connecting to Tor…", badge **TOR · RECONNECTING**) rather than connect without it. A node on
this computer is the exception (badge **LOCAL NODE**): Tor can't reach it, and its traffic never
leaves the machine. A wallet already open keeps its route until it is opened again.

If a firewall or security product blocks `tor`, Settings says so within seconds, with the file to
allow. Prefer your own Tor (Tor Browser listens on `127.0.0.1:9150`)? Choose **My own SOCKS
proxy** instead. What Tor does and doesn't hide: [SECURITY.md](SECURITY.md#built-in-tor).

<br clear="right"/>

### 11) Coin control: freeze coins

<img src="docs/screenshots/wallet-coins.png" width="50%" align="right" alt="Coins: four coins, the freelance one frozen" />

Every payment you receive is its own **coin**. The **Coins** tab lists the account's unspent coins:
the amount, the address it arrived at (by its label: a quick reminder of who knows about that coin),
its confirmations, and whether it is still maturing.

- **Freeze** a coin and the wallet leaves it out of every transaction, **Send max** included, until
  you **Unfreeze** it. The balance card shows how much is frozen, and the send confirmation mentions
  frozen coins it left out.
- Why bother, with ring signatures? A transaction that spends several of your coins together tells
  its recipient that they belong to one person. Keep a coin from an exchange, or from a job you'd
  rather keep separate, out of everyday payments by freezing it.
- Frozen stays frozen: the list is sealed in your vault (the wallet file is shredded on lock) and
  applied again as soon as the wallet finds those coins after an unlock, and again right before
  every send.
- A watch-only wallet lists its coins but can't freeze them (that takes the spend key).

<br clear="right"/>

### 12) Keyboard shortcuts

| Keys (Cmd on macOS) | Does |
|---|---|
| **Ctrl+1** … **Ctrl+6** | Receive, Send, History, Contacts, Tools, Coins |
| **Ctrl+R** | Refresh now |
| **Ctrl+L** | Lock the vault (every wallet) |
| **Ctrl+H** | Hide or show amounts |

They work wherever the focus is, a text box included. The window also reopens at the size you left
it, and while a wallet catches up the sync line estimates the time left.

---

## Screenshot tour

Every screen below is rendered from the app's real views by `tools/UiSnapshots` (headless, demo data
on stagenet; the same run fails on any binding error), so what you see is what ships.

| | |
|---|---|
| <img src="docs/screenshots/startup.png" alt="Startup checks" /><br/>**Startup** checks for monero-wallet-rpc and your node. | <img src="docs/screenshots/startup-installing.png" alt="Installing monero-wallet-rpc" /><br/>**Download & install** monero-wallet-rpc, verified against binaryFate's key. |
| <img src="docs/screenshots/create-vault-seed.png" alt="Writing down the seed" /><br/>**Create a vault**: the seed comes from monero-wallet-rpc itself. | <img src="docs/screenshots/create-vault-duress.png" alt="The duress password" /><br/>**Duress password**: a decoy that looks like the real thing. |
| <img src="docs/screenshots/unlock.png" alt="Unlock" /><br/>**Unlock**: either password, same screen, same timing. | <img src="docs/screenshots/wallet-receive.png" alt="Receive" /><br/>**Receive**: QR code, full address, the route badge (TOR). |
| <img src="docs/screenshots/wallet-receive-request.png" alt="Payment request" /><br/>**Request a payment**: amount and description in the QR code. | <img src="docs/screenshots/wallet-receive-addresses.png" alt="Subaddresses" /><br/>**Subaddresses**, labelled, used ones marked. |
| <img src="docs/screenshots/wallet-send-multi.png" alt="Send to two recipients" /><br/>**Send** to several people in one transaction. | <img src="docs/screenshots/wallet-send-confirm-multi.png" alt="Confirm a send" /><br/>**Confirm** with the exact fee, before anything is broadcast. |
| <img src="docs/screenshots/wallet-sent.png" alt="Sent" /><br/>**Sent**, with the payment proof ready to share. | <img src="docs/screenshots/wallet-history-details.png" alt="History" /><br/>**History**: filters, search, details and private notes. |
| <img src="docs/screenshots/wallet-coins.png" alt="Coins" /><br/>**Coins**: freeze the ones you don't want spent. | <img src="docs/screenshots/wallet-contacts.png" alt="Contacts" /><br/>**Contacts**, shared by the wallets of a password. |
| <img src="docs/screenshots/wallet-tools.png" alt="Tools" /><br/>**Tools**: prove a payment, sign and check messages. | <img src="docs/screenshots/wallet-tools-proofs.png" alt="Reserve proofs" /><br/>**Reserve proofs**: prove a balance, check someone's. |
| <img src="docs/screenshots/wallet-manage.png" alt="Manage" /><br/>**Manage**: name, node, accounts, backup, remove. | <img src="docs/screenshots/wallet-accounts.png" alt="Accounts" /><br/>**Accounts**: separate balances inside one wallet. |
| <img src="docs/screenshots/add-wallet.png" alt="Add a wallet" /><br/>**Add a wallet**: new, from seed, from keys, watch-only. | <img src="docs/screenshots/wallet-watch-only.png" alt="Watch-only" /><br/>**Watch-only**: sees what arrives, can't spend. |
| <img src="docs/screenshots/settings-tor.png" alt="Built-in Tor" /><br/>**Built-in Tor**, connected. | <img src="docs/screenshots/settings-tor-installing.png" alt="Installing Tor" /><br/>**Download & install Tor**, verified against Tor Project's key. |
| <img src="docs/screenshots/settings-tor-blocked.png" alt="Tor blocked" /><br/>**Blocked by a firewall?** It says which file to allow. | <img src="docs/screenshots/wallet-tor-connecting.png" alt="Waiting for Tor" /><br/>**Fail closed**: a wallet waits for Tor rather than go direct. |

---

## What syncs from where (restore heights)

| Seed | Scans from | Why |
|---|---|---|
| **Generated** (real or decoy) | The node's tip **just before the seed was created** (never more than the clock-based chain estimate), minus 720 blocks (~1 day) | A fresh seed cannot have earlier history; the margin absorbs a reorg or a node slightly ahead, and the cap stops a lying node from hiding incoming payments |
| **Imported real** | **Your choice**: full history (default) / a specific block / now (minus the same margin) | You know your wallet's age; full history is the safe default |
| **Imported decoy** (when creating the vault) | **Always full history** | A decoy that hides its own funds is a broken decoy |
| **Added later** (restored from seed or keys, watch-only) | **Your choice**: full history (default) or a specific block | The same rules as an import; a height above the chain tip is refused as a likely typo |

Only the seed (or keys) persist, so **every unlock re-scans from that height**. A wallet created recently
syncs in seconds; one restored from full history takes as long as the node needs every time. Switching
**network** on the create screen resets generated seeds and heights.

## Where your data lives

| Path | What | Secret? |
|---|---|---|
| `%APPDATA%\XaultWallet\vault.xv` (Linux: `~/.config/XaultWallet/`; macOS: `~/Library/Application Support/XaultWallet/`) | Your encrypted vault — the **only** persistent wallet data | Encrypted (AES-256-GCM, Argon2id) |
| `…\XaultWallet\settings.json` | Binary path, default node, refresh/auto-lock intervals, Tor/proxy choice, window size | No secrets, plain JSON |
| `…\XaultWallet\logs\` | Diagnostic log | Seeds/passwords/keys redacted; nothing per-wallet (no heights, nodes or send events) |
| `%TEMP%\xaultwallet_*` (per open wallet) | wallet-rpc's restored wallet files, its log, its ring database | **Shredded** (overwritten + deleted) on lock/exit; private to your user |
| `%LOCALAPPDATA%\XaultWallet\monero-cli\` (Linux: `~/.local/share/XaultWallet/monero-cli/`) | `monero-wallet-rpc`, only if *Download & install* put it there | Not secret (an official, signature-checked binary) |
| `%LOCALAPPDATA%\XaultWallet\tor\` (Linux: `~/.local/share/XaultWallet/tor/`) | `tor` and its GeoIP files, only if *Download & install Tor* put them there | Not secret (Tor Project's signature-checked build) |
| `%LOCALAPPDATA%\XaultWallet\tor-data\` | Tor's own state (consensus cache, guard choice), while built-in Tor is used | Not secret; shows that Tor is used on this computer |

On Linux/macOS the `XaultWallet` folder is `0700` and its files `0600` regardless of your umask (older
installs are tightened at startup); exports and seed backups you save are written `0600` too.

**Privacy notes:** a public node's operator can see your IP and the transactions you broadcast (not
your balance or history) — and, with several wallets open, that the same computer is syncing all of
them. For privacy, run your own node, or turn on the built-in Tor (or your own SOCKS proxy) in
Settings: the node then sees a Tor exit, not your IP address.
Deleting `vault.xv` without a seed backup means the funds are gone — the seed *is* the wallet.

## Security model in one page

- **At rest:** seeds and keys sealed with AES-256-GCM; key derived by Argon2id (256 MiB / 4
  iterations). A wrong password is detected only by an authentication-tag failure — **no plaintext
  password comparison**.
- **Deniability:** two equal slots of 256 KiB each (whatever they hold; 4 KiB in a vault from 0.2/0.3
  until you upgrade it), order randomised, unused slot random. Both slots' plaintexts have the same
  fields; every operation is symmetric; a duress unlock takes the same time as a normal one. Limits
  (snapshots over time, a wipe flag examined before it fires, a vault from 0.3 used with only one of
  its passwords since, several wallets on one remote node): [SECURITY.md](SECURITY.md).
- **In memory:** passwords/keys pass through pinned, zero-on-dispose buffers — best-effort in a managed runtime.
- **On the wire:** wallet-rpc binds `127.0.0.1` on a random port, exists only while unlocked, and
  requires **per-session random digest credentials** (never on its command line). Before a seed is sent,
  the app checks the port belongs to the process it started (Linux, Windows). Browser-based attacks on
  the local RPC (cross-site requests, DNS rebinding) are refused.
- **Over Tor:** with built-in Tor on, node traffic, node checks and downloads go only through it,
  and wait while it connects (fail closed). The `tor` binary is installed only after Tor Project's
  signature (Tor Browser Developers key, pinned) and the download's checksum verify:
  [SECURITY.md](SECURITY.md#built-in-tor).
- **Coin control:** frozen coins are never spent; the frozen list is sealed in the vault, in a field
  every slot has, so it says nothing about which profile is real.
- **Not protected against:** malware on your machine, an attacker with both your vault and your
  password, and coercion that doesn't stop at the decoy. No software fixes those.

## Architecture

```
XaultWallet.Core                ← class library, no UI deps, unit-tested
├── Security/
│   ├── VaultManager          create / unlock / open a session / change password / duress policy (all symmetric)
│   ├── VaultSession          an open profile: save its own slot with the unlock key, check / change its password
│   ├── VaultFile             on-disk format 2: magic XVLT, two equal 256 KiB slots, randomized order (keeps format 1 until upgraded)
│   ├── SlotPayload           the sealed JSON (v3: wallets + contacts, one field set for every slot; reads v1, v2)
│   ├── VaultCrypto           Argon2id (bounded params) + AES-256-GCM
│   ├── SecureBuffer          pinned, zeroed memory for secrets
│   ├── SecureDelete          best-effort overwrite-then-delete
│   └── PasswordStrength      conservative entropy estimate + pattern discounts
├── Models/                   WalletProfile, WalletSecrets (seed / keys / watch-only), Contact, SeedOffsetPolicy
├── Installer/                fetch + verify (binaryFate's OpenPGP signature, SHA-256) + install monero-wallet-rpc
├── Tor/                      TorInstaller (Tor Project's detached signature + SHA-256), TorProcess (a SOCKS-only tor child)
└── Monero/
    ├── MoneroWalletService   generate / validate / open / accounts / subaddresses / multi-recipient send / relay / proofs
    ├── MoneroProcessManager  authenticated loopback wallet-rpc child; private session dir shredded on dispose
    ├── LoopbackPortOwnership "is that port really our child?" (Linux /proc, Windows GetExtendedTcpTable)
    ├── MoneroRpcClient       hand-built JSON-RPC envelope; digest-only credentials; no proxy
    ├── XmrAmount / BlockHeight  culture-independent parsing of what users type
    ├── MoneroAddress         sanity checks only (length/charset/prefix) — never checksum crypto
    ├── MoneroUri             monero: payment links (build, and parse strictly)
    ├── DaemonAddress         the one definition of a valid node URL
    └── SecretRedactor        structural redaction of secrets in RPC JSON

XaultWallet.Desktop             ← Avalonia 11, MVVM (CommunityToolkit.Mvvm)
├── Startup / Unlock / Create / Wallet / AddWallet / Settings views + view-models
├── Profile (the open vault: wallets, contacts, auto-lock) → one Wallet view-model per open wallet
├── Views/Wallet/             Receive, Send, History, Contacts, Tools, Coins tabs; the Manage sheet
├── TorController             the app's own Tor: start with the app, the one route while on (fail closed)
├── Controls/                 Icon (line icons), QrCodeView (QRCoder)
└── The UI is IDENTICAL for the real and duress profiles — by construction

tests/   XaultWallet.Core.Tests        unit tests (vault, deniability, crypto, parsing, hardening)
         XaultWallet.IntegrationTests  real monero-wallet-rpc on a private regtest chain
installer/ XaultWallet.iss, .wxs       the Windows installers (Inno Setup, WiX), built by the release workflow
tools/   TestRunner                    reflection test runner (fails on zero discovered tests)
         UiSnapshots                   renders every screen headlessly; fails on binding errors / QR mismatch
```

## Build from source

Requires the **.NET 10 SDK**.

```bash
./build.sh                                      # restore, build (warnings = errors), unit tests
dotnet run --project src/XaultWallet.Desktop    # run the app
```

- **Visual Studio 2022** (17.8+): open `XaultWallet.sln`, F5. Windows: `./build.ps1`.
- **Single-file release** for your platform: `./publish-windows.ps1` / `./publish-linux.sh`, or
  `dotnet publish src/XaultWallet.Desktop -c Release -r <win-x64|linux-x64|osx-arm64>`.
- **Windows installers**: publish `win-x64` into `out/`, copy the docs next to the exe, then
  `iscc /DAppVersion=0.6.0-beta /DAppNumericVersion=0.6.0 /DSourceDir=%CD%\out installer\XaultWallet.iss`
  (Inno Setup 6) for the setup exe, and see the header of `installer/XaultWallet.wxs` for the MSI
  (WiX Toolset 3). The release workflow builds both and test-installs each.
- **UI snapshots** (every screen, zero-binding-error check, QR round-trip with `zbarimg`):
  `dotnet run -c Release --project tools/UiSnapshots -- ui-snapshots`
- **Integration tests** against a real `monero-wallet-rpc` — a private regtest chain is the quickest:
  see [STAGENET-TESTING.md](STAGENET-TESTING.md#automated-integration-tests-regtest).
- **End-to-end tests** of the whole app through its UI (create, duress, receive, send, history, a
  second wallet, contacts, a two-recipient send), on Linux in process and on Windows against the
  released exe: see
  [STAGENET-TESTING.md](STAGENET-TESTING.md#end-to-end-tests-the-whole-app-through-its-ui).

CI (GitHub Actions) runs all of the above on every push: unit tests on Linux and Windows, the regtest
integration and end-to-end tests on Linux, the UI smoke test, and the Windows end-to-end test of the
published exe. A release is drafted only after that Windows test passes on the exact zip being
released; see [RELEASE.md](RELEASE.md#automated-releases-github-actions).

## Troubleshooting

| Symptom | Cause & fix |
|---|---|
| **"Couldn't start the wallet"** banner | XaultWallet can't find/launch `monero-wallet-rpc`. **Open Settings**, set the binary path (or *Download & install*), **Test**, then **Retry**. |
| **"Another program is answering on the wallet backend's port"** | Something else grabbed the random port before wallet-rpc could. Close other wallet software and **Retry** — a fresh port is chosen each time. |
| Stuck on **"Connecting to node…"** | The node is down, syncing, or on the wrong network. **Manage** → *Node*: *Test*, or switch this wallet to another node ([How-to #6](#6-several-wallets-add-switch-manage)). A node that doesn't answer at all holds that wallet's monero-wallet-rpc for each connection attempt (about 20 s on Windows, up to 2 min on Linux) before anything else it's asked gets an answer: *Test* a node before you apply it. |
| **"The vault is full"** | One password's profile has to fit in 256 KiB: hundreds of wallets, contacts and notes do, but not without limit. Remove some (long notes first), then try again. Nothing was written. |
| **"This vault still has the format of XaultWallet 0.3…"** | That format holds about 4 KB per password. Settings → **Vault format** → *Upgrade* makes room; if you have a duress password, read [Coming from 0.3](#9-coming-from-03) first. Nothing was written. |
| **"Your firewall or security software is blocking tor"** | Allow the `tor` file named in the message to make outgoing connections (Windows Defender Firewall, or your security product), then **Start / restart** in Settings. |
| **"Connecting to Tor…" never finishes** | Settings → Network & privacy shows Tor's own progress. A network that blocks Tor can't be helped by this release (no bridges yet): use **My own SOCKS proxy** with a Tor that has bridges, or **Direct**. |
| **A local node stops working with Tor on** | It shouldn't: a node at `127.0.0.1` is reached directly. A node elsewhere on your LAN goes through Tor, which can't reach private addresses: use its public address, or choose **Direct** for it. |
| **Watch-only balance looks too high** | A watch-only wallet sees what it receives but not what was spent (that needs the spend key). Its figure is labelled *received*. |
| **Imported wallet shows 0 balance** | Wrong **seed offset**, wrong **network**, or a too-recent **scan from** choice. Re-import with *Full history* and compare the derived address at the confirmation step. |
| **Balance says maturing** | Fresh coins need 10 confirmations (~20 min) to unlock; change and mining rewards too. |
| **Send fails: "Not enough spendable balance…"** | Amount + exact fee exceeds what's unlocked. Lower the amount or wait for funds to mature. |
| **Broadcast failed / interrupted** | The message shows the txid — check **History** (or an explorer) before retrying, so you don't pay twice. |
| **Forgot the vault password** | Unrecoverable by design. Restore from your 25-word seed into a fresh vault. |
| **Forgot a seed-offset passphrase** | Unrecoverable — the 25 words alone open a different wallet. Monero's design, not the app's. |
| Log files (`…/XaultWallet/logs`) | Safe to share when reporting bugs: no seeds, passwords, keys, nodes or per-wallet details. Still, skim before posting. |

## FAQ

**Why should I supply `monero-wallet-rpc` myself?**
Because you shouldn't have to trust a random wallet's bundled crypto binary. You download it from
getmonero.org, verify the signature yourself, and this app just drives it. If you'd rather not, the
built-in *Download & install* fetches the same official build and verifies it the same way (Monero's
release signature and checksum) before installing it; it never bundles one.

**Is the duress wallet detectable?**
Not from the vault file, and not from the decoy's own contents: the slots are equal-size, padded,
position-randomised, and decrypt to the same shape; the unused slot is random noise. That holds however
many wallets and contacts each password's profile has. What remains is
opsec (a bank statement showing 10 XMR bought while the decoy holds 0.1 is the giveaway) plus the
limits in [SECURITY.md](SECURITY.md) — notably copies of the vault taken at different times.

**Why isn't Tor bundled in the release?**
For the same reason monero-wallet-rpc isn't: you shouldn't have to trust a wallet's copy of someone
else's network binary. *Download & install Tor* fetches Tor Project's own build and installs it only
after Tor Project's signature and the checksum verify, and you can point Settings at a tor you
installed yourself.

**Does Monero need coin control?**
Less than Bitcoin, because ring signatures and stealth addresses already hide which outputs a
transaction spends from everyone else. But the recipient of a transaction that combines several of
your coins learns they belong together. Freezing keeps a coin you want separate out of it.

**Can I use this on mainnet?**
The UI allows it behind red warnings, but the honest answer is: **don't**. It's unaudited beta.

**Does the tx key let someone spend my funds?**
No. A transaction key proves one specific payment and nothing else. Never share the 25-word seed or
the spend key (or, for privacy, the view key).

## Roadmap

- Faster unlocks for old wallets (advance the stored restore height past spent history, or an opt-in
  encrypted wallet cache)
- Signed releases and reproducible builds
- Verified `.onion` node presets, and Tor bridges for networks that block Tor
- Spending only the coins you pick in a send (coin selection), on top of freezing
- Key-image import for watch-only wallets (so they can see spends), and offline signing
- Hardware wallets (Ledger / Trezor through monero-wallet-rpc)
- Screen-capture protection while the seed is shown
- Opt-in fiat display (off by default — it would call a price API, which is a privacy trade-off)
- The big one: a **professional third-party security audit** before any mainnet story exists

## Contributing, license, acknowledgements

This is a personal, educational project shared in the open. Review, issues, and PRs are welcome —
extra eyes on a self-custody wallet are exactly the point of open-sourcing it. For anything
security-sensitive, use the private reporting process in [SECURITY.md](SECURITY.md) instead of a
public issue. Release notes: [CHANGELOG.md](CHANGELOG.md).

**License:** [MIT](LICENSE) — provided as-is, no warranty.

Built on the official Monero tools (`monerod`, `monero-wallet-rpc`) — this project drives them rather
than reimplementing Monero's cryptography. UI built with [Avalonia](https://avaloniaui.net/); QR codes
by [QRCoder](https://github.com/codebude/QRCoder). Screenshots are rendered from the app's real views
with demo data on stagenet. Brought to life in harmony — <https://dboudreau.dev>

XaultWallet is client-only: no service, no fee, no churning or mixing. It drives the official Monero
binary and nothing more.
