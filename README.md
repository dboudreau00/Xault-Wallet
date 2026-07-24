# XaultWallet

**A privacy-first desktop Monero (XMR) wallet with a duress password that opens a decoy wallet.**
Built on .NET 8 + Avalonia. Encrypted at rest with AES-256-GCM (Argon2id KDF). Drives the official
`monero-wallet-rpc` — it never reimplements Monero's cryptography.

![License: MIT](https://img.shields.io/badge/license-MIT-green)
![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4)
![Avalonia 11](https://img.shields.io/badge/Avalonia-11-8B44AC)
![Status](https://img.shields.io/badge/status-unaudited%20beta-red)
![Platform](https://img.shields.io/badge/release-Windows%20x64-blue)

<!-- Screenshots are embedded in THIS file (hosted on GitHub's asset CDN, with local originals in
     docs/screenshots/). Because the tags live in the README itself, uploading a new build can no
     longer wipe them off the GitHub page. -->
<img width="1070" height="756" alt="XaultWallet dashboard" src="https://github.com/user-attachments/assets/5dd0fce3-0940-40a7-8f77-2ee846520654" />

> ## ⚠️ Unaudited beta — do NOT use with real funds
>
> This is an **educational, work-in-progress** wallet. It has **not** had a professional security
> audit. It may contain bugs that cause **permanent, irreversible loss of funds** — Monero
> transactions cannot be reversed or refunded.
>
> - **Do not store real (mainnet) XMR in it.** Use **testnet** or **stagenet** only.
> - Provided **as-is, with no warranty of any kind** (see [LICENSE](LICENSE)).
> - The **"wipe real wallet on duress"** option is irreversible and destroys your seed on that device.
> - Always keep an **independent offline backup** of your 25-word seed.
>
> Want a wallet for actual funds? Use an established, audited one (official Monero GUI/CLI,
> Feather, Cake). Read [SECURITY.md](SECURITY.md) in full before doing anything with this project.

---

## Contents

- [Highlights](#highlights)
- [How it works (trust model)](#how-it-works-trust-model)
- [Quick start (Windows, ~10 minutes)](#quick-start-windows-10-minutes)
- [How-to guides](#how-to-guides)
  - [Create your first wallet (generated seed)](#1-create-your-first-wallet-generated-seed)
  - [Import an existing wallet (with optional seed-offset passphrase)](#2-import-an-existing-wallet-with-optional-seed-offset-passphrase)
  - [Set up the duress (decoy) password](#3-set-up-the-duress-decoy-password)
  - [Receive and send](#4-receive-and-send)
  - [Prove or verify a payment](#5-prove-or-verify-a-payment)
  - [Change an existing wallet's node](#6-change-an-existing-wallets-node)
  - [Change your master password](#7-change-your-master-password)
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
| 🔐 **Encrypted vault** | Your seed is sealed with **AES-256-GCM**, key derived by **Argon2id** (256 MiB, 4 iterations). The only file that persists is `vault.xv`. |
| 🎭 **Duress password** | A second password opens a **decoy wallet** that looks completely normal. The vault always contains two equal, indistinguishable slots — an attacker can't prove a hidden wallet exists. Optional: opening the decoy can **wipe** the real slot. |
| 🧾 **Exact fee before you send** | The send confirmation builds the real transaction first (unbroadcast) and shows the **exact fee and total**. Confirm broadcasts that same signed transaction — the fee cannot change after you've seen it. |
| 🧭 **Address echo on import** | Importing a seed shows the **derived primary address** and asks you to confirm it matches before anything is saved — catching a wrong seed-offset, a typo'd seed, or a wrong network *before* funds are involved. |
| 🔑 **Payment proofs** | One click surfaces the **transaction key** after a send (safe to share — it cannot spend), and a verify panel checks anyone's payment given txid + key + address. |
| 🌐 **Your node, your choice** | Curated public-node presets or your own `monerod`. The node of an **existing** wallet can be changed later (password-gated, deniable). |
| 🧹 **Hygiene by default** | Wallet files live in an ephemeral temp dir, **shredded on lock**. Copied addresses/keys **auto-clear from the clipboard after 30 s**. Logs structurally **redact seeds, passwords, and keys**. Auto-lock on inactivity. |
| 🚫 **No hand-rolled crypto** | All key derivation, signing, and address logic is done by the **official `monero-wallet-rpc`** you supply and verify yourself. |

## How it works (trust model)

The most dangerous thing a wallet author can do is reimplement Monero's cryptography. XaultWallet
deliberately does not. It launches the **official `monero-wallet-rpc`** binary (which you download
from [getmonero.org](https://www.getmonero.org/downloads/) and verify yourself) as a **loopback-only
child process** on a random local port, restores your wallet from seed into a **temporary directory**,
and talks JSON-RPC to it. That child syncs against a `monerod` node — yours, or a public one.

```
┌─────────────────┐   JSON-RPC (127.0.0.1, random port)   ┌────────────────────┐        ┌─────────┐
│   XaultWallet    │ ─────────────────────────────────────▶│  monero-wallet-rpc  │ ─────▶ │ monerod │
│  (this project)  │                                       │  (official, yours)  │        │ (a node)│
└────────┬────────┘                                       └────────────────────┘        └─────────┘
         │ owns: encrypted vault (seed at rest), duress logic, UI
         │ never: keys, signing, address derivation — that's Monero's official code
```

XaultWallet's own code is responsible for exactly three things: the **encrypted vault format**, the
**duress/deniability logic**, and the **UI**. Everything cryptographic about Monero itself is the
official tools' job.

## Quick start (Windows, ~10 minutes)

**You need three things:** this release, the official Monero CLI tools, and a node to sync against.

### Step 1 — Get the release

Download the latest `XaultWallet-win-x64-<date>.zip` from the releases page and extract it anywhere.
The single `XaultWallet.exe` is self-contained — **no .NET install required** (Windows 10/11 x64).

### Step 2 — Get and verify the official Monero CLI tools

1. Download the **Monero CLI** (not GUI) for Windows from <https://www.getmonero.org/downloads/>.
2. **Verify the download** — this wallet's whole trust model rests on that binary being genuine.
   Follow the official guide: <https://www.getmonero.org/resources/user-guides/verification-windows-beginner.html>
   (import the signing key, check the hash of your zip against the signed `hashes.txt`).
3. Extract it. You only need one file from it: **`monero-wallet-rpc.exe`**. Note its full path.

> Details and alternatives: [SETUP-MONERO-RPC.md](SETUP-MONERO-RPC.md)

### Step 3 — First launch & settings

1. Run `XaultWallet.exe`. The splash checks for the binary and a node; on a fresh machine both
   checks fail — that's expected.
2. Open **Settings** (top-right).
3. **Monero-wallet-rpc executable** → *Browse…* to your `monero-wallet-rpc.exe` → **Test binary**.
   You should see its version (e.g. `Monero 'Fluorine Fermi' (v0.18.x)`).
4. **Default node** → pick a preset from the dropdown — for your first run choose a **testnet**
   preset (e.g. *MoneroDevs (testnet)*) — then **Test node**. You should see its block height.
5. **Save**, then **Close**.

### Step 4 — Create a wallet on testnet

Follow [How-to #1](#1-create-your-first-wallet-generated-seed) below. Keep the network on
**Testnet** — testnet coins are worthless by design, which is exactly what you want while learning.

### Step 5 — Get testnet coins and play

Grab coins from a community faucet (search "monero testnet faucet" / "monero stagenet faucet" —
faucets come and go), or ask in Monero community channels. Then exercise the full loop: receive,
watch it confirm, send some back, lock, unlock. A full walkthrough with node/env details lives in
[STAGENET-TESTING.md](STAGENET-TESTING.md).

---

## How-to guides

### 1) Create your first wallet (generated seed)

1. On the create screen, choose your **network** (stay on testnet/stagenet — mainnet shows a red
   warning for a reason) and a **node** (preset dropdown, or type your own `http://host:port`).
2. Under **Real wallet**, keep **Create new** selected and click **Generate new seed**.
   The 25 words appear in a numbered grid. The seed is generated by `monero-wallet-rpc` itself —
   never by this app's code.
3. **Write the 25 words down, in order, on paper.** Then either:
   - **Verify backup** — retype three randomly-chosen words, or
   - **Download backup (.txt)** — saves a plaintext file (it says loudly that it's plaintext;
     store it offline and delete stray copies).
   One of the two is required before the vault can be created.
4. Choose a **main password** (minimum 8 characters; the strength meter estimates entropy — aim
   high, this is what protects your seed at rest).
5. (Optional but recommended) set up the **duress password** — see [How-to #3](#3-set-up-the-duress-decoy-password).
6. Click **Create vault**. You'll land on the unlock screen; unlock and the dashboard opens while
   the wallet scans in the background.

> **Note:** a freshly generated wallet only scans from its creation moment ("newest block only") —
> there is nothing older to find, so first sync is fast. If you switch network *after* generating,
> the seed grid clears and you must generate again — heights from one chain must never be sealed
> against another.

### 2) Import an existing wallet (with optional seed-offset passphrase)

1. Under **Real wallet**, select **Import seed** and paste your 25 words.
2. **Seed offset passphrase** — leave this **blank** unless the wallet was created elsewhere *with*
   an offset (a.k.a. "passphrase" / "25th word"). If it was: enter it **exactly** — it is
   case- and space-sensitive, it is a **separate secret** the 25 words cannot recover, and a wrong
   or missing offset silently opens a different, **empty** wallet with no error.
3. **Sync from** — pick how far back to scan:
   - **Full history** (default) — always finds everything. Slowest, safest.
   - **From a specific block** — enter your wallet's creation height if you know it.
   - **From now** — only for a brand-new seed with no history; existing funds will NOT appear.
4. Set your password(s) and click **Create vault**.
5. **The address-confirmation step:** XaultWallet opens the seed once (via `monero-wallet-rpc`) and
   shows you the **primary address it derives**. **Check it against the wallet you meant to
   import.** If it doesn't match — wrong offset, typo, wrong network — click **Cancel**, fix the
   input, and try again. Nothing is saved until you confirm.

### 3) Set up the duress (decoy) password

The duress password opens a second, fully-functional wallet that is indistinguishable in the UI.
Under coercion you type the duress password instead of the real one.

1. On the create screen, tick **Set up a duress (decoy) password**.
2. Choose a duress password (min 8 chars, must differ from the main password).
3. Give the decoy a seed: **Generate decoy** (fresh, empty wallet) or **Import decoy** (a seed you
   control that holds a plausible small amount — an imported decoy always scans full history so its
   funds are guaranteed to show).
4. Download the decoy's backup too, and consider keeping a little XMR on it — an empty decoy is
   less convincing.
5. **"Wipe the real wallet on duress"** — read twice before ticking. When the decoy is opened,
   the real slot is overwritten with random data **permanently, on that device**. Only enable it if
   your real seed is backed up somewhere else. This cannot be undone.

**Deniability, honestly stated:** the vault file always contains two equal-size slots; without a
duress wallet the second slot is random filler that is cryptographically indistinguishable from an
encrypted wallet. Which password opens what is determined only by which slot's authentication tag
verifies — there is no password list, no flags, no metadata. See [SECURITY.md](SECURITY.md) for
what this does and does not protect against.

### 4) Receive and send

**Receive** — the Receive tab shows your primary address; **Copy address** puts it on the clipboard
(auto-clears after 30 s). **New subaddress** generates a fresh, unlinkable address for the same
wallet — good practice is a new subaddress per counterparty.

**Send** —
1. Paste the destination address and amount, pick a priority, click **Review & send**.
   The address is sanity-checked (length/charset/network prefix) and the transaction is **built and
   signed but NOT broadcast**.
2. The confirmation shows the destination, the **exact network fee**, and the **total**. These are
   locked into the signed transaction — what you see is what you pay.
3. **Send now** broadcasts exactly that transaction. **Cancel** discards it; nothing touched the
   network.
4. After a send, the **payment proof** panel shows the txid and transaction key.

If a broadcast fails or is interrupted, the message includes the txid and tells you to check the
History tab before retrying — an early retry of a transaction that actually went through would
build a **second** payment.

<img width="1074" height="758" alt="Creating the encrypted vault" src="https://github.com/user-attachments/assets/5a27f93f-3477-4fba-98a1-093bb667916a" />

### 5) Prove or verify a payment

Monero is private — a recipient cannot see who paid them. To **prove** your payment: share the
**transaction ID** and **transaction key** (shown after every send, or fetched later via
*Fetch key from wallet* for any tx this wallet sent). The tx key is safe to share: it proves that
one payment and **cannot spend anything**. Never share your seed or spend key with anyone.

To **verify** a payment someone claims they made to an address: enter their txid, tx key, and the
destination address in the *Verify a payment* panel — the wallet reports exactly how much that
address received in that transaction and its confirmations.

### 6) Change an existing wallet's node

Settings → **Change this wallet's node** (only shown when a vault exists):

1. Pick a preset or enter a node URL — **same network as the wallet** — and **Test node**.
2. Enter your wallet password and click **Update node**. Takes effect at the next unlock.

This works with **either** the main or the duress password and repoints whichever wallet that
password opens — so performing it reveals nothing about which wallet is which, even to someone
watching. (The *Default node* setting above it only affects wallets you create later.)

### 7) Change your master password

Settings → **Change master password**. Re-encrypts the real wallet under the new password; your
seed does not change. The duress password (if set) is unaffected — and deliberately cannot be used
here: only the real wallet's password can change the real wallet's password.

<img width="1072" height="752" alt="Unlock screen" src="https://github.com/user-attachments/assets/86803c4a-e383-4145-a116-03956b5c0ded" />

---

## What syncs from where (restore heights)

Sync behavior is deterministic by seed provenance — no surprises:

| Seed | Scans from | Why |
|---|---|---|
| **Generated** (real or decoy) | Its **own creation moment** ("newest block only"), clamped to the chain tip | A fresh seed cannot have earlier history; scanning the past would be pure waste |
| **Imported real** | **Your choice**: full history (default) / a specific block / from now | You know your wallet's age; full history is the safe default |
| **Imported decoy** | **Always full history** | A decoy that hides its own funds is a broken decoy |

Switching **network** on the create screen resets generated seeds and heights — a block height from
one chain is meaningless (and dangerous) on another.

## Where your data lives

| Path | What | Secret? |
|---|---|---|
| `%APPDATA%\XaultWallet\vault.xv` | Your encrypted vault — the **only** persistent wallet data | Encrypted (AES-256-GCM, Argon2id) |
| `%APPDATA%\XaultWallet\settings.json` | Binary path, default node, refresh/auto-lock intervals | No secrets, plain JSON |
| `%APPDATA%\XaultWallet\logs\` | Diagnostic log | Seeds/passwords/keys are **structurally redacted** before they can ever reach it |
| *(temp, per session)* | `monero-wallet-rpc`'s wallet files, restored from seed on unlock | **Shredded** (overwritten + deleted) on lock/exit |

**Privacy notes:** a public node's operator can see your IP and the transactions you broadcast (not
your balance or history). For mainnet-grade privacy, run your own node. Deleting `vault.xv` without
a seed backup means the funds are gone — the seed *is* the wallet.

## Security model in one page

- **At rest:** seed sealed with AES-256-GCM; key derived by Argon2id (256 MiB / 4 iterations —
  deliberately heavy). A wrong password is detected only by an authentication-tag failure; there is
  **no plaintext password comparison anywhere** in the codebase.
- **Deniability:** two equal 4096-byte-padded slots, order randomized, unused slot filled with
  random bytes. Slot metadata (real vs decoy, wipe policy) lives *inside* the encrypted payload.
- **In memory:** passwords/keys pass through pinned, zero-on-dispose buffers. This is best-effort
  in a managed runtime — see [SECURITY.md](SECURITY.md) for honest limits.
- **On the wire:** the wallet-RPC child is bound to `127.0.0.1` on a random port and exists only
  while the wallet is unlocked. The JSON it exchanges is redacted of secrets before any error
  message or log line can carry it.
- **Not protected against:** malware on your machine (a keylogger gets your password), an attacker
  who has both your vault and your password, and rubber-hose attacks that don't stop at the decoy.
  No software fixes those.

## Architecture

```
XaultWallet.Core                ← class library, no UI deps, unit-tested
├── Security/
│   ├── VaultManager          create / unlock / change-password / repoint-node / duress policy
│   ├── VaultFile             on-disk format: magic XVLT, two equal padded slots, randomized order
│   ├── VaultCrypto           Argon2id (bounded params) + AES-256-GCM
│   ├── SecureBuffer          pinned, zeroed memory for secrets
│   └── PasswordStrength      conservative entropy estimate
├── Models/                   WalletSecrets, SeedOffsetPolicy, networks
└── Monero/
    ├── MoneroWalletService   generate / validate / open / balance / prepare-send / relay / proofs
    ├── MoneroProcessManager  loopback-only monero-wallet-rpc child; shreds temp dir on dispose
    ├── MoneroRpcClient       hand-built JSON-RPC envelope; SecretRedactor on every error path
    ├── MoneroAddress         sanity checks only (length/charset/prefix) — never checksum crypto
    └── DaemonAddress         the one definition of a valid node URL

XaultWallet.Desktop             ← Avalonia 11, MVVM (CommunityToolkit.Mvvm)
├── Startup / Unlock / Create / Wallet / Settings views + view-models
└── The UI is IDENTICAL for the real and duress wallets — by design

tests/                          ← xUnit: vault, crypto, duress, redaction, policy (always run)
                                  + integration tests (env-gated on XW_WALLET_RPC / XW_DAEMON)
```

## Build from source

Requires the **.NET 8 SDK**. First build needs internet once (NuGet).

```bash
dotnet test                                     # build + all unit tests
dotnet run --project src/XaultWallet.Desktop    # run the app
```

- **Visual Studio 2022** (17.8+, ".NET desktop development" workload): open `XaultWallet.sln`, F5.
- **Windows release** (single-file, self-contained): `./publish-windows.ps1` → `release/win-x64/XaultWallet.exe`
- **Linux**: `./publish-linux.sh` (build from source; the packaged release is Windows-only)
- Integration tests run only when `XW_WALLET_RPC` / `XW_DAEMON` / `XW_NETWORK` are set — see
  [STAGENET-TESTING.md](STAGENET-TESTING.md).

## Troubleshooting

| Symptom | Cause & fix |
|---|---|
| **"The wallet backend didn't start"** banner | XaultWallet can't find/launch `monero-wallet-rpc`. Click **Open Settings** in the banner, set the binary path, **Test binary**, then **Retry** (Retry picks up the new path immediately). |
| Stuck on **"Connecting to node…"** | The node is down, syncing, or on the wrong network. Settings → *Test node*; try another preset; repoint the wallet ([How-to #6](#6-change-an-existing-wallets-node)). |
| **Imported wallet shows 0 balance** | Wrong **seed offset** (even one character/space), wrong **network**, or a too-recent **sync-from** choice. Re-import with *Full history* and check the derived address at the confirmation step against your known address. |
| **Balance says locked / not spendable** | Fresh coins need ~10 confirmations (~20 min) to unlock; change and mining rewards too. Wait — this is Monero, not the wallet. |
| **Send fails: "Not enough spendable balance…"** | Amount + exact fee exceeds what's unlocked. Lower the amount or wait for balance to unlock. |
| **Broadcast failed / interrupted** | The message shows the txid — check the **History** tab (or an explorer) before retrying, so you don't accidentally pay twice. |
| **Forgot the main password** | The vault is unrecoverable by design. Restore from your 25-word seed backup into a fresh vault. No seed backup = funds gone. |
| **Forgot a seed-offset passphrase** | Unrecoverable — the 25 words alone open a different wallet. This is Monero's design, not the app's. |
| Log files (`%APPDATA%\XaultWallet\logs`) | Safe to share when reporting bugs: seeds/passwords/keys are redacted at the source (`"seed":"***"`). Still, skim before posting. |

## FAQ

**Why do I have to supply `monero-wallet-rpc` myself?**
Because you shouldn't trust a random wallet's bundled crypto binary. You download it from
getmonero.org, verify the signature yourself, and this app just drives it. A bundled binary would
break the chain of trust this project is built on.

**Is the duress wallet detectable?**
Not from the vault file: both slots are equal-size, padded, and position-randomized; the unused
slot is random noise. Detectability in practice depends on your opsec (e.g. a bank statement
showing you bought 10 XMR while the decoy holds 0.1 is the giveaway — keep the decoy plausible).

**Can I use this on mainnet?**
The UI allows it behind a red warning, but the honest answer is: **don't**. It's unaudited beta.

**Does the tx key let someone spend my funds?**
No. A transaction key proves one specific payment and nothing else. The things that must never be
shared are the 25-word seed, the spend key, and (for privacy) the view key.

**Windows only?**
The packaged release is Windows x64. The code is cross-platform .NET 8 / Avalonia and builds on
Linux/macOS from source (`publish-linux.sh` included), but only Windows is exercised regularly.

## Roadmap

- Subaddress list with labels; address book
- Optional SOCKS5 proxy setting for Tor (bring-your-own-Tor — never bundled)
- Opt-in fiat display (off by default — it would call a price API, which is a privacy trade-off)
- The big one: a **professional third-party security audit** before any mainnet story exists

## Contributing, license, acknowledgements

This is a personal, educational project shared in the open. Review, issues, and PRs are welcome —
extra eyes on a self-custody wallet are exactly the point of open-sourcing it. For anything
security-sensitive, use the private reporting process in [SECURITY.md](SECURITY.md) instead of a
public issue.

**License:** [MIT](LICENSE) — provided as-is, no warranty.

Built on the official Monero tools (`monerod`, `monero-wallet-rpc`) — this project drives them
rather than reimplementing Monero's cryptography. UI built with [Avalonia](https://avaloniaui.net/).
Brought to life in harmony — <https://dboudreau.dev>



XaultWallet is client-only: No service, no fee, no churning or mixing. It drives the official Monero binary and nothing more.
