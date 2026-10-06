# Security model & limitations

This document is deliberately blunt. A wallet's job is to protect money, and the fastest way
to lose money is to overestimate what a piece of software actually guarantees.

## What is protected

**Encryption at rest.** The vault file is sealed with **AES-256-GCM**. The 256-bit key is
derived from your password with **Argon2id** (default: 256 MiB memory, 4 iterations, 4 lanes),
which is memory-hard and resists GPU/ASIC brute-forcing far better than PBKDF2 or bcrypt.
Every slot has its own random 16-byte salt and 12-byte nonce. GCM is *authenticated*, so any
tampering with the file is detected on unlock rather than silently producing garbage.

**No plaintext password handling in the security core.** Passwords are converted to a pinned,
zeroable buffer, run through the KDF, and the derived key is used directly. "Is this the right
password?" is answered by whether the GCM authentication tag verifies — never by comparing
stored password material.

**What a password opens.** Each password opens a *profile*: one or more wallets (from a seed, from
keys, or watch-only), an address book, and the subaddress labels, account names and transaction
notes of each wallet. All of it is sealed together in that password's slot. In a vault created by
0.5, or upgraded to its format, every slot is 256 KiB of encrypted data however much it holds, so
the file is the same size for one wallet or twenty, with or without contacts. A vault from 0.2 or
0.3 keeps its 4 KiB slots until you upgrade it ([Upgrading to 0.5](#upgrading-to-05-vault-format-2)).
When a profile no longer fits, the app refuses the change and says so; nothing is written.

**Duress / plausible deniability.** The file always contains two equal-sized slots. Without a
correct password an adversary cannot tell whether the second slot is a decoy profile or random
filler, cannot tell which physical slot is real (position is randomised at write time), and
cannot tell how many wallets exist. Unlock does constant work across all slots.

The duress scenario is the one where the adversary DOES hold a password — the duress one — plus
the device. So the decoy must also be indistinguishable from the inside:

- Every slot decrypts to the same JSON shape (payload v3: a list of wallets and contacts, with no
  "real"/"decoy" kind anywhere). An examiner who decrypts the decoy with the duress password sees
  exactly what a single-password vault looks like. **Vaults created by 0.1 are the exception until
  each slot has been converted** — see [Upgrading from 0.1](#upgrading-from-01).
- Every operation is symmetric: unlocking, adding, renaming or removing wallets, contacts, labels,
  notes, a wallet's node, and changing the password work identically for either profile, with
  identical wording. An open profile re-seals **only its own slot**, with the key derived at unlock
  (no Argon2 per change); the other slot is copied back byte for byte. Changing the password from
  inside an open wallet changes that profile's password only, whichever it is. A duress unlock does
  the same key-derivation work as a normal one.
- Wipe-on-duress fires on ANY use of the duress password, then clears its own flag (re-sealing with
  the key already derived, so it takes no extra time). Afterwards the vault is a single-wallet vault.
- The app's log records nothing that differs between the two wallets (no nodes, restore heights or
  send events).

**The local wallet RPC.** monero-wallet-rpc listens on `127.0.0.1` only while a wallet is open, and
requires per-session random HTTP Digest credentials. They are passed through a private config file
(`0600`, inside a `0700` session folder) rather than the command line — other local users can read a
process's command line — and the file is shredded once the server is up. This blocks web pages
(cross-site POSTs, DNS rebinding) and other local users from driving the open wallet. Digest auth
cannot authenticate the *server* (monero sends no `rspauth`), so before any seed is sent — and before
a new seed is generated — the app checks that every listener that could answer on that port belongs
to the process it started and that the process is still running (Linux, Windows). The check fails
closed: if ownership can't be confirmed — including on macOS, where the check is not yet
implemented — nothing is sent. wallet-rpc's own log and ring database
are kept in the same session folder, which is shredded on lock.

**The wallet-rpc binary.** It is only ever started from a fully-qualified path. A bare name in
Settings ("monero-wallet-rpc") is looked up on `PATH`; a relative path is refused. Handing a
relative name to the OS would make it search the app's folder and then the *current directory*,
so a file planted wherever the app was launched from could be started and handed the seed.

## What is NOT protected — read this

**Memory forensics.** .NET is a garbage-collected runtime. `SecureBuffer` pins and zeroes the
buffers *it* owns, but the moment a password exists as a `string` (e.g. the unlock field bound
in `UnlockViewModel`) the CLR may have already made immutable copies on the managed heap that
we cannot reliably wipe. The Argon2id library (Konscious) also keeps an internal copy of the
password bytes that is not zeroed after key derivation. A determined attacker with a memory
dump of the running, *unlocked* process can likely recover secrets. Locking the wallet and
closing the app is your defence; an unlocked wallet on a compromised machine is compromised.

**A compromised operating system.** Keyloggers, malicious kernels, screen capture, and
hypervisor-level attackers defeat any user-space wallet. This app cannot protect a password
typed into a machine that is already owned. Other programs in your desktop session can also read
what the app shows (screen capture, or accessibility APIs for visible text such as your seed words
while they are displayed). Masked password boxes are the exception: since 0.3 they accept input from
assistive tools but never hand their text out.

**Secure deletion on SSDs.** The temp-file shredder overwrites bytes before deleting, but on
SSDs with wear-levelling, and on copy-on-write or journaling filesystems, the original blocks
may physically remain. Treat "shred" as best-effort, not a guarantee. For strong guarantees,
use full-disk encryption underneath this app.

**Several wallets on one remote node.** Each open wallet runs its own monero-wallet-rpc and syncs
on its own (the one on screen every few seconds, the others once a minute). A node you don't run
sees all of them asking from the same IP address at the same times, and can reasonably guess they
belong to one person. Use your own node, or a SOCKS proxy such as Tor (Settings), if your wallets
must not be linked. Wallets start syncing only once you open them after unlocking.

**What you export or share.** History CSV exports include your transaction notes, in plain text.
A payment proof (transaction key) proves one payment and nothing else; a reserve proof tells whoever
checks it how much the account holds (all of it when you don't give an amount); a signed message
proves you control the address. A watch-only wallet's private view key, like a seed, is sealed in
the vault: anyone holding it sees every payment the wallet receives.

**Deniability against a sophisticated adversary.** The design defeats inspection of the vault
file, including by someone holding the duress password. It does **not** defeat an adversary who
can observe your daemon (a remote node sees your wallet's sync requests and broadcasts), correlate
on-chain activity, image your RAM while unlocked, or find OS-level artifacts (recent-files lists,
swap, crash dumps, file-system journals). Specifically:

- **Copies of the vault taken at different times** show which slots changed. If both slots ever
  changed between two copies (e.g. you changed both passwords), both are live wallets. Hidden
  volumes share this limitation. Since 0.5 more actions change a slot: adding or editing a wallet,
  contact, label or note, handing out a subaddress, changing a wallet's node, even switching wallets
  (the vault remembers the one you used last) re-seal that password's slot. Simply unlocking does
  not, apart from wipe-on-duress and three one-time cases: the first unlock of a 0.1 slot, the first
  save 0.5 makes in a 0.2/0.3 slot, and the first time 0.5 opens a wallet restored from a seed (it
  records the wallet's main address, so that adding the same wallet again by its keys is caught).
- **A wipe flag examined before it fires.** If you enable wipe-on-duress, an examiner who decrypts the
  decoy *offline* — without the app ever opening it — can read `wipeOther: true` and infer a second
  wallet existed. The instruction has to be readable with the duress password; once the app opens the
  decoy, the flag is consumed and gone. Without wipe-on-duress the decoy is indistinguishable.
- **macOS:** the port-ownership check is not implemented there. The app fails closed: it refuses to
  send a seed (or generate one) to the wallet backend until a macOS ownership check exists. Releases
  still build an osx-arm64 binary, but seed-bearing use on macOS is non-functional until then.
- **Vaults upgraded from 0.1** — see [Upgrading from 0.1](#upgrading-from-01).
- **The ring database** now lives only for the session. Monero keeps it so a wallet re-uses the same
  rings across a chain split; losing it each session only matters during a contentious fork.

True hidden-volume deniability à la VeraCrypt is a much harder problem than this addresses. If
your threat model includes a state-level adversary with physical access, do not rely on this
alone.

**The "wipe on duress" option is irreversible.** If you enable it, any use of the duress
password — unlocking, changing its password or its node — permanently destroys the real slot on
that device, along with vault copies the app kept beside it (Settings → Restore). Exported backups
stored elsewhere are out of its reach. If your seed is not backed up elsewhere, your funds are gone.
This is a feature, and it is a foot-gun: never use the duress password yourself.

**Weak passwords.** Argon2id raises the cost per guess, but a short or common password is
still guessable offline by anyone with a copy of the vault. Use a long, high-entropy passphrase.
The app refuses the weakest passwords (common words, sequences, keyboard runs, repeats), but its
estimator is a heuristic, not a full dictionary model (`zxcvbn` would be better).

**What else is on disk (and unencrypted).** Besides the encrypted vault, XaultWallet writes
two plaintext files under `%APPDATA%/XaultWallet/` (`~/.config/XaultWallet/` on Linux,
`~/Library/Application Support/XaultWallet/` on macOS):
`settings.json` (your monero-wallet-rpc path, default node, network, intervals, proxy) and `logs/`
(high-level events and error types). These deliberately contain **no** secrets — no seeds,
passwords, keys, or RPC credentials — and nothing that distinguishes one wallet from the other, so
they are not encrypted. While a wallet is open, a private `xaultwallet_*` folder in your temp
directory holds wallet-rpc's restored wallet, its log and ring database; it is shredded on lock and
swept on the next launch after a crash. If you download a seed backup, that file *is* plaintext by
design; store it offline and delete any on-disk copy.

**File permissions (Linux/macOS).** The data folder is `0700` and everything the app writes in it —
vault, settings, logs, kept vault copies — is `0600`, whatever your umask. Installs made by earlier
versions are tightened at startup. Vault exports, seed backups and history exports are also written
`0600` when the save dialog hands back a local path. (Before this, a umask of `022` — the common
default — left the vault and any seed backup readable by every account on the machine.) On Windows
the per-user profile ACLs on `%APPDATA%` provide the equivalent; the app changes nothing there.

## Upgrading from 0.1

0.2 changed what is sealed inside each slot (payload v2, above). The app can only re-seal a slot
while it is open, so a vault created by 0.1 is converted **one slot at a time, each by its own
password**:

- The first unlock with a password converts that password's slot. The app then shows a one-time
  notice — the *same* notice for either password, so it says nothing about which wallet opened.
- **Until the duress password is used once in 0.2, a decoy slot made by 0.1 still contains
  `"kind":1` (and `"duressWipeReal"`) inside its encryption.** Anyone holding the vault file and the
  duress password can read that and know a second wallet exists — the exact leak 0.2 removes.
- Using the duress password converts its slot — and, if you enabled wipe-on-duress in 0.1, also
  wipes the main wallet, exactly as it always would have. Don't "unlock with it once to upgrade it"
  while that option is on.
- Copies made before the upgrade — exported backups, file-system snapshots, sync/cloud history —
  stay in the 0.1 format forever.

If you never set a duress password in 0.1, the upgrade is complete after your first unlock. If you
did, the clean fix is to rebuild the vault:

1. Have both seeds (main and decoy) and their restore heights written down, and check them.
2. Settings → **Open data folder**, quit the app, and move `vault.xv` somewhere offline as a fallback.
3. Start the app (it now offers to create a vault), choose **Import** for the main seed and for the
   decoy seed, and set both passwords.
4. Unlock with each password to confirm the right wallet opens. Then destroy the old `vault.xv` and
   every exported copy of it.

## Upgrading to 0.5 (vault format 2)

0.5 seals a whole profile in each password's part of the vault, which needs more room than 0.2 and
0.3 gave it. A vault they wrote (format 1: 4 KiB per password) opens in 0.5 and **stays in format 1
until you upgrade it** in Settings → *Vault format* (shown while one of its wallets is open). Nothing
about the file changes on its own.

- **In the old format** each password's part holds about 4 KB: a wallet or two with some labels,
  notes and contacts. A change that doesn't fit is refused, nothing is written, and the message
  points to the upgrade. 0.5 saves a password's part in its own layout the first time it writes it
  (the first time it opens each of its wallets, to record the wallet's main address, and on any
  change). 0.3 can still open the file and any part 0.5 hasn't saved; it refuses a saved one as
  "created by a newer version of XaultWallet", and changes nothing.
- **Upgrading** turns the file into format 2 (256 KiB per password, as every vault 0.5 creates). The
  part your password opens is re-sealed in it. The **other part is carried over byte for byte**, at
  the start of its new, larger slot (the rest is random), and is re-sealed when its own password next
  opens it; until then it opens as before, because every slot is tried both ways on every unlock.
- **There is no way back.** 0.3 and earlier can't open a format 2 file: they report that the vault
  file has an unexpected size, and change nothing. If you might return to 0.3, export a backup
  (Settings → Export backup) first, and keep it until you're sure.
- **A second password.** Until it has been used in 0.5, its part shows that — to whoever can open
  that part, which is to say whoever holds that password: in the old format its contents are still
  in 0.3's layout, and after an upgrade it is a carried part. That is exactly the duress scenario: an
  examiner with the duress password and the vault file could see that the decoy hasn't been used
  since the vault was, and conclude that another password has been. So:
  - **Without wipe-on-duress:** unlock with the duress password in 0.5 before anything else, and wait
    until its wallet is ready (its address shows). If you want the bigger format, upgrade from there.
    Then lock and use your main password as usual: its part is re-sealed when it opens.
  - **With wipe-on-duress:** don't: any use of the duress password wipes the main wallets. Upgrade
    from your main password if you need the room, knowing an examiner who decrypts the decoy can tell
    — as they can already read its wipe flag (see above). For a vault without either sign, rebuild it
    in 0.5 from both seeds (the steps under [Upgrading from 0.1](#upgrading-from-01)).
- Vaults created by 0.1 open the same way, and everything in
  [Upgrading from 0.1](#upgrading-from-01) still applies to them.

## The monero-wallet-rpc installer

Bringing your own monero-wallet-rpc, downloaded and verified yourself, remains the recommendation:
then you, not this app, decide what you trust. For everyone else, *Download & install* (on the
startup screen and in Settings) does what a careful person does by hand, and installs nothing unless
every step succeeds:

1. It fetches `https://www.getmonero.org/downloads/hashes.txt` and checks its OpenPGP signature
   against **binaryFate's key, which ships inside the app** (Monero's own `utils/gpg_keys/binaryfate.asc`)
   and is accepted only for the pinned fingerprints `81AC 591F E9C4 B65C 5806 AFC3 F0AF 4D46 2A0B DF92`
   (primary key) and `AD56 4CDA 8F16 65AC E78B 5DFD 2593 838E ABB1 F655` (signing subkey). The
   verifier is deliberately narrow (v4 RSA, canonical-text signatures, SHA-256/512) and refuses
   anything else, including text outside the signed block.
2. It downloads this computer's official CLI archive from `downloads.getmonero.org` and requires its
   SHA-256 to be the signed one.
3. It extracts only `monero-wallet-rpc`, runs it once with `--version` (after the checks, never
   before), and installs it in your user profile (on Windows `%LOCALAPPDATA%\XaultWallet\monero-cli`,
   on Linux `~/.local/share/XaultWallet/monero-cli`), replacing an older one — but never a *newer*
   one already on disk, and never a release older than the app's built-in minimum
   (`WalletRpcInstaller.MinimumVersion`). The signature's creation time is not checked (the
   verifier does not parse it).

What this trusts: the copy of binaryFate's key in the XaultWallet release you are running (verify
that release's `SHA256SUMS.txt`), and that key itself. A build signed with it is accepted; one that
isn't, isn't. getmonero.org sees your IP address unless you set a SOCKS proxy (Settings), which the
download then uses.

## Choosing a daemon

A remote/public node can see which blocks your wallet asks about and your broadcast
transactions' timing/origin. For maximum privacy run your own `monerod`, or route the daemon
connection over Tor. XaultWallet passes your daemon address straight through to
`monero-wallet-rpc`; it does not add network-level privacy on its own.

**Encryption to the node.** An `http://` node address is unencrypted: anyone on the network path
sees (and can alter) the wallet's traffic with that node. The built-in presets are all `http://`.
For an `https://` address the app starts monero-wallet-rpc with `--daemon-ssl enabled` (and asks
for the same when you switch a wallet's node): the connection must be TLS with a certificate that
verifies against your system's certificate authorities for that host name, or it fails. Without it,
wallet-rpc's default would accept an unverified certificate and fall back to plain HTTP if the TLS
handshake failed, so an attacker on the path could quietly remove the encryption. A node with a
self-signed certificate therefore does not connect over `https://`. A `.onion` node reached through
Tor (Settings → SOCKS proxy) is encrypted and authenticated by Tor itself.

A node also supplies the chain height a *new* seed starts scanning from. A height above the block
that holds a payment would hide that payment until the wallet is restored with an earlier height,
so the app never accepts more than the clock-based estimate monero's own wallet uses
(`wallet2::get_approximate_blockchain_height`), minus a one-day margin. A node can still lie in many
other ways (balances, history, fees); a node you don't run is trusted for all of them.

## Before trusting this with real funds

1. Have the crypto core and duress logic **independently audited**.
2. Test the full send/receive flow on **stagenet** first.
3. Keep an **offline backup of your 25-word seed**. The vault is a convenience layer; the seed
   is the source of truth.
4. Run under **full-disk encryption**.

## Reporting a vulnerability

**Please do not open a public GitHub issue for security bugs.** Public disclosure before a fix
puts any user at risk.

Instead, report privately:

- Open a **GitHub private security advisory** (repo → **Security → Report a vulnerability**), or
- Contact the maintainer at **https://dboudreau.dev**.

Please include what you found, how to reproduce it, and the potential impact. As an unpaid
solo/beta project there is no bug-bounty, but credit will be given (if you want it) once a fix
is released. Please allow a reasonable window for a fix before any public disclosure.

Highest-priority areas: the vault format and its Argon2id/AES-256-GCM usage, the two-slot
duress design (does it actually resist distinguishing the real slot from the decoy — including by
someone holding the duress password?), the irreversible "wipe on duress" path, the local RPC
authentication and port-ownership check, and how long the seed/password persist in process memory.

The October 2026 internal review and its fixes are documented in [docs/AUDIT-2026-10.md](docs/AUDIT-2026-10.md).
