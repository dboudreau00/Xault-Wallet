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

**Deniability against a sophisticated adversary.** The two-slot design defeats a casual
inspection of the vault file. It does **not** defeat an adversary who can observe your daemon
(a remote node sees your wallet's view-key sync requests), correlate on-chain activity, image
your RAM while unlocked, or find OS-level artifacts (recent-files lists, swap, crash dumps).
True hidden-volume deniability à la VeraCrypt is a much harder problem than this addresses. If
your threat model includes a state-level adversary with physical access, do not rely on this
alone.

**The "wipe on duress" option is irreversible.** If you enable it, entering the duress
password permanently destroys the real slot on that device. If your seed is not backed up
elsewhere, your funds are gone. This is a feature, and it is a foot-gun.

**Weak passwords.** Argon2id raises the cost per guess, but a short or common password is
still guessable. Use a long, high-entropy passphrase. The strength meter is conservative but
is not a full dictionary/keyboard-walk analyser (wire in `zxcvbn-cs` for production).

**What else is on disk (and unencrypted).** Besides the encrypted vault, XaultWallet writes
two plaintext files under `%APPDATA%/XaultWallet/`: `settings.json` (your monero-wallet-rpc
path, default daemon, network, refresh interval) and `logs/` (high-level events and error
types). These deliberately contain **no** secrets — no seeds, passwords, keys, or RPC
credentials — so they are not encrypted. If you download a seed backup, that file *is*
plaintext by design; store it offline and delete any on-disk copy.

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

## Responsible disclosure

If you find a vulnerability, do not open a public issue. Contact the maintainer privately and
allow time for a fix before disclosure.
