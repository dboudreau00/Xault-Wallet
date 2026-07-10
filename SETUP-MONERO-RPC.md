# Setting up monero-wallet-rpc (read before mainnet)

XaultWallet drives the official `monero-wallet-rpc` binary; it does **not** bundle it, and you
should not trust a copy from anyone (including this project) without verifying it yourself. The
binary handles your keys — a tampered copy can steal every seed you generate. Verifying the
maintainers' signature is the one step that protects you, so it is not optional for real funds.

## 1. Download from the official source only

Get the CLI archive for your OS from **one** of these (identical binaries; GitHub is preferred
because you don't have to trust the website too):

- GitHub: https://github.com/monero-project/monero/releases
- Website: https://www.getmonero.org/downloads/

The archive contains both `monerod` (the node) and `monero-wallet-rpc` (what XaultWallet needs).

## 2. Verify the download (do not skip for mainnet)

Monero publishes a signed `hashes.txt`. Verify the archive's hash matches, and verify the
signature on `hashes.txt` was made by the Monero maintainer key (fingerprint published at
https://www.getmonero.org/downloads/#pgp and in the `binaryFate` key).

Monero's own step-by-step guides:
- Windows (beginner): https://www.getmonero.org/resources/user-guides/verification-windows-beginner.html
- Command line (all platforms): https://www.getmonero.org/resources/user-guides/verification-allos-advanced.html

Short version once you have the maintainer key imported and `hashes.txt` + its `.sig`:

```
gpg --verify hashes.txt.sig hashes.txt      # must say "Good signature" from the Monero key
# then confirm your archive's hash is listed:
#   Windows PowerShell:  Get-FileHash .\monero-win-x64-*.zip -Algorithm SHA256
#   Linux/macOS:         shasum -a 256 monero-*.tar.bz2
```

Only proceed if the signature is good **and** your file's hash appears in `hashes.txt`.

## 3. Extract and point XaultWallet at it

Extract somewhere stable and **not** inside a cloud-synced folder (OneDrive/Dropbox will fight
the daemon's data files). Then in XaultWallet: **Settings → monero-wallet-rpc executable →**
paste the full path to `monero-wallet-rpc(.exe)` → **Test binary** (should report the version).

## 4. Run a node for your network

XaultWallet launches its own short-lived `monero-wallet-rpc`; you only need to run the node:

```
# testnet  (fast, no value — start here)
monerod --testnet
# stagenet (mainnet-like, no value)
monerod --stagenet
# mainnet  (real funds — only after you've tested the above)
monerod
```

Default daemon addresses XaultWallet expects (auto-filled by the network picker):
`mainnet http://127.0.0.1:18081` · `stagenet http://127.0.0.1:38081` · `testnet http://127.0.0.1:28081`

Use **Settings → Test daemon** to confirm it responds before creating a wallet. Let the node
finish syncing (`SYNCHRONIZED OK`) so balances and sends work.

## 5. Choosing mainnet

The create screen shows a red warning whenever mainnet is selected. That warning is serious:
this is unaudited beta software, transactions are irreversible, and the "wipe on duress" option
permanently destroys the real seed on the device. Before mainnet:

- Complete the full flow on **stagenet** at least once (receive, send, restart, restore).
- Keep an **independent** written backup of any seed you generate.
- Start with a **small** amount you can afford to lose.
- Get a **professional security audit** before trusting meaningful funds — see `SECURITY.md`.

Remote nodes are convenient but leak your IP/transaction origin to the node operator. For
mainnet privacy, run your own node (optionally over Tor). See `SECURITY.md`.
