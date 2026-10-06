# Testing XaultWallet on stagenet

Stagenet is Monero's testing network. Coins have no value, so it's the right place to
exercise a wallet end to end without risking anything. **Do not test with mainnet funds.**

## 1. Get the Monero tools

Download an official Monero CLI release from getmonero.org, verify it, and note the path to
`monero-wallet-rpc` (or `monero-wallet-rpc.exe` on Windows) — or let the app's **Download &
install** fetch and verify it for you. You do not need to build anything — XaultWallet drives this
binary directly. (For a local stagenet node you need `monerod` from the same archive.)

## 2. Run a stagenet daemon

```bash
monerod --stagenet
# wait for it to sync; it will serve RPC on 127.0.0.1:38081 by default
```

You can point at a remote stagenet node instead, but a local one is simplest and most private.

## 3. Configure XaultWallet

Launch the app, click **Settings**, and:

1. Under **Wallet backend**, set the **monero-wallet-rpc** path (or leave it blank if it's on your
   PATH) and click **Test** — you should see the version string.
2. Under **Network & privacy**, choose **Stagenet** with `http://127.0.0.1:38081` (or a stagenet
   preset) and click **Test** — you should see the daemon's current height.
3. **Save changes**.

## 4. Create a wallet

1. On the create screen, keep **Create a new wallet** selected and click **Generate my seed**.
2. Write the 25 words down, then either pass the three-word **verification** or **save the backup
   file**. (The file is plaintext by design — treat it like cash.)
3. Set a strong main password. Optionally set a duress password + decoy seed.
4. **Create vault**, then unlock with your password.

## 5. Exercise it

- **Receive:** copy your address from the Receive tab. Get stagenet coins from a faucet
  (search "monero stagenet faucet") and send them to it.
- **Sync:** watch the balance appear as the wallet syncs (status shows the height).
- **Send:** send some back to the faucet or another stagenet address. Confirm the tx hash
  and fee are reported and the balance updates.
- **History:** confirm incoming/outgoing transfers show in the History tab.
- **Restart:** lock, close the app, reopen, unlock. The wallet should restore from seed and
  resync. Nothing but the encrypted vault should exist on disk between runs.
- **Duress (if set):** unlock with the duress password and confirm you get the decoy wallet.
  If you enabled "wipe real on duress", verify (with a *throwaway* vault) that the real slot
  is destroyed — this is irreversible, so test it only on a disposable vault.
- **Several wallets:** add a second wallet (**+** next to the wallet's name), switch between them,
  lock and unlock (the vault reopens on the wallet you used last), and check the duress password's
  profile shows none of them.
- **Contacts and payment requests:** save a contact and *Pay* it; request an amount on Receive and pay
  it by pasting the copied `monero:` link into another wallet's Send; pay two recipients at once.
- **Tools:** prove a payment from History (*Prove this payment*), sign and check a message, make and
  check a reserve proof.
- **A vault from 0.3** (a copy — keep the original): it opens in 0.5 unchanged, and Settings shows
  *Vault format*. Following [Coming from 0.3](README.md#9-coming-from-03), unlock with the duress
  password first, upgrade from there, then unlock with the main password; both open their wallets,
  and the file is now 256 KiB per password. 0.3 should refuse the upgraded copy.

## Automated integration tests (regtest)

The `XaultWallet.IntegrationTests` project drives the real `monero-wallet-rpc`. The quickest
setup is a **private regtest chain** — no download, no network, blocks mined on demand — which is
exactly what CI runs on every push:

```bash
monerod --regtest --offline --fixed-difficulty 1 --data-dir /tmp/xw-regtest \
        --rpc-bind-ip 127.0.0.1 --rpc-bind-port 18081 --non-interactive --detach

export XW_WALLET_RPC=/path/to/monero-wallet-rpc
export XW_DAEMON=http://127.0.0.1:18081
export XW_NETWORK=regtest
dotnet test tests/XaultWallet.IntegrationTests -c Release
```

On regtest the suite mines coins to a fresh wallet and runs a full money flow — prepare a send,
check the exact fee, relay it, prove it with the tx key, check history, sweep everything back — plus
the security checks: the backend refuses unauthenticated and cross-origin requests, keeps its files
inside the shredded session folder, and never puts its password on the command line. An impostor that
grabs the backend's port first never receives the seed, both when it answers at once and when it
waits for the real backend to die before answering.

Against stagenet instead, point `XW_DAEMON` at a synced stagenet node and set `XW_NETWORK=stagenet`;
the funds-flow tests are skipped (they need to mine). Without the variables every test no-ops with a
skip notice, so a green run with nothing configured means "skipped", not "passed".

## End-to-end tests (the whole app, through its UI)

One scenario (`tests/E2E.Shared/WalletScenario.cs`) uses the app the way a person does: create a
vault, write down and verify the seed, set a duress password with a decoy, unlock both wallets,
receive (mined) coins, copy the address, send 12.5 XMR to the decoy with the confirmation checked line
by line, see it in History, see the decoy receive it, open Settings. Then the 0.5 features: add a
second wallet (new seed, backup checked), switch back, save the decoy as a contact, pay the contact and
the second wallet in one transaction, see the second wallet receive its share, lock, check the vault
reopens on the wallet used last with the contact still there, and check the duress password still opens
only the decoy (paid twice, with no contacts). Mining is the only thing done outside the UI. It needs
the regtest node above and `monero-wallet-rpc`, and leaves a screenshot of every step. Two drivers run
it:

**Linux, in process** (CI runs this on every push). Real pointer and keyboard input; a fresh profile in
a temporary home, never yours:

```bash
XW_E2E_DAEMON=http://127.0.0.1:18081 \
  dotnet run -c Release --project tests/XaultWallet.E2E.Headless -- e2e-screens
```

**Windows, the real `XaultWallet.exe`**, driven from outside through UI Automation
(`.github/workflows/e2e-windows.yml` runs it on every push and before every release, against the exact
release zip). It refuses to run if `%APPDATA%\XaultWallet` already exists, so it can't touch a real
vault; run it on a fresh VM:

```powershell
# monerod --regtest running as above. With no monero-wallet-rpc.exe next to XaultWallet.exe, the
# scenario starts by installing it with the app's own "Download & install" (from getmonero.org).
$env:XW_E2E_DAEMON = "http://127.0.0.1:18081"
dotnet run -c Release --project tests/XaultWallet.E2E.Windows -- C:\path\to\XaultWallet.exe e2e-screens
```

After the scenario it closes the app with its close button and fails if `monero-wallet-rpc` is still
running, a session folder is left in `%TEMP%`, or anything was written next to the exe. The screenshots
are the run's `e2e-screens-windows` artifact; run the workflow by hand with *publish_screenshots* (or
put `[e2e-screenshots]` in a pushed commit message) to also get them on the `ci/e2e-screenshots`
branch.

## What still stands between this and mainnet

Even after stagenet passes cleanly, this is **beta**. Before trusting real funds it needs a
**professional third-party security audit** and broader real-world testing. See `SECURITY.md`.
