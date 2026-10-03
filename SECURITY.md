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
  examiner who decrypts the decoy with the duress password sees exactly what the only wallet of a
  single-wallet vault looks like. (Vault payload v1 stored `"kind":1` in the decoy; v1 slots are
  re-sealed as v2 the first time their password is used.)
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
(cross-site POSTs, DNS rebinding) and other local users from driving the open wallet. Before any
seed is sent, the app checks that the listening socket belongs to the process it started (Linux,
Windows), so a process that grabbed the port first cannot receive it. wallet-rpc's own log and ring
database are kept in the same session folder, which is shredded on lock.

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
- **macOS:** the port-ownership check is not implemented there.
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

## Choosing a daemon

A remote/public node can see which blocks your wallet asks about and your broadcast
transactions' timing/origin. For maximum privacy run your own `monerod`, or route the daemon
connection over Tor. XaultWallet passes your daemon address straight through to
`monero-wallet-rpc`; it does not add network-level privacy on its own.

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
