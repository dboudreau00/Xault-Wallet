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

**Duress / plausible deniability.** The file always contains two equal-sized slots. Without a
correct password an adversary cannot tell whether the second slot is a decoy wallet or random
filler, cannot tell which physical slot is real (position is randomised at write time), and
cannot tell how many real wallets exist. Unlock does constant work across all slots.

The duress scenario is the one where the adversary DOES hold a password — the duress one — plus
the device. So the decoy must also be indistinguishable from the inside:

- Every slot decrypts to the same JSON shape (payload v2: no "real"/"decoy" kind, no label). An
  examiner who decrypts a decoy created by 0.2 with the duress password sees exactly what the only
  wallet of a single-wallet vault looks like. **Vaults created by 0.1 are the exception until each
  slot has been converted** — see [Upgrading from 0.1](#upgrading-from-01).
- Every operation is symmetric: unlock, change password and change node work identically for either
  slot, with identical wording. A duress unlock does the same key-derivation work as a normal one.
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
closed: if ownership can't be confirmed, nothing is sent. wallet-rpc's own log and ring database
are kept in the same session folder, which is shredded on lock.

**The wallet-rpc binary.** It is only ever started from a fully-qualified path. A bare name in
Settings ("monero-wallet-rpc") is looked up on `PATH`; a relative path is refused. Handing a
relative name to the OS would make it search the app's folder and then the *current directory*,
so a file planted wherever the app was launched from could be started and handed the seed.

## What is NOT protected — read this

**Memory forensics.** .NET is a garbage-collected runtime. `SecureBuffer` pins and zeroes the
buffers *it* owns, but the moment a password exists as a `string` (e.g. bound to a text box)
the CLR may have already made immutable copies on the managed heap that we cannot reliably
wipe. A determined attacker with a memory dump of the running, *unlocked* process can likely
recover secrets. Locking the wallet and closing the app is your defence; an unlocked wallet on
a compromised machine is compromised.

**A compromised operating system.** Keyloggers, malicious kernels, screen capture, and
hypervisor-level attackers defeat any user-space wallet. This app cannot protect a password
typed into a machine that is already owned.

**Secure deletion on SSDs.** The temp-file shredder overwrites bytes before deleting, but on
SSDs with wear-levelling, and on copy-on-write or journaling filesystems, the original blocks
may physically remain. Treat "shred" as best-effort, not a guarantee. For strong guarantees,
use full-disk encryption underneath this app.

**Deniability against a sophisticated adversary.** The design defeats inspection of the vault
file, including by someone holding the duress password. It does **not** defeat an adversary who
can observe your daemon (a remote node sees your wallet's sync requests and broadcasts), correlate
on-chain activity, image your RAM while unlocked, or find OS-level artifacts (recent-files lists,
swap, crash dumps, file-system journals). Specifically:

- **Copies of the vault taken at different times** show which slots changed. If both slots ever
  changed between two copies (e.g. you changed both passwords), both are live wallets. Hidden
  volumes share this limitation.
- **A wipe flag examined before it fires.** If you enable wipe-on-duress, an examiner who decrypts the
  decoy *offline* — without the app ever opening it — can read `wipeOther: true` and infer a second
  wallet existed. The instruction has to be readable with the duress password; once the app opens the
  decoy, the flag is consumed and gone. Without wipe-on-duress the decoy is indistinguishable.
- **macOS:** the port-ownership check is not implemented there; the app relies on the per-session
  digest credentials alone.
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
two plaintext files under `%APPDATA%/XaultWallet/` (`~/.config/XaultWallet/` on Linux):
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
did, the clean fix is to rebuild the vault in 0.2:

1. Have both seeds (main and decoy) and their restore heights written down, and check them.
2. Settings → **Open data folder**, quit the app, and move `vault.xv` somewhere offline as a fallback.
3. Start the app (it now offers to create a vault), choose **Import** for the main seed and for the
   decoy seed, and set both passwords.
4. Unlock with each password to confirm the right wallet opens. Then destroy the old `vault.xv` and
   every exported copy of it.

## Choosing a daemon

A remote/public node can see which blocks your wallet asks about and your broadcast
transactions' timing/origin. For maximum privacy run your own `monerod`, or route the daemon
connection over Tor. XaultWallet passes your daemon address straight through to
`monero-wallet-rpc`; it does not add network-level privacy on its own.

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
