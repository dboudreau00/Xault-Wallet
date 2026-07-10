# Testing XaultWallet on stagenet

Stagenet is Monero's testing network. Coins have no value, so it's the right place to
exercise a wallet end to end without risking anything. **Do not test with mainnet funds.**

## 1. Get the Monero tools

Download an official Monero CLI release from getmonero.org and note the path to
`monero-wallet-rpc` (or `monero-wallet-rpc.exe` on Windows). You do not need to build
anything — XaultWallet drives this binary directly.

## 2. Run a stagenet daemon

```bash
monerod --stagenet
# wait for it to sync; it will serve RPC on 127.0.0.1:38081 by default
```

You can point at a remote stagenet node instead, but a local one is simplest and most private.

## 3. Configure XaultWallet

Launch the app, click **Settings**, and:

1. Set the **monero-wallet-rpc** path (or leave blank if it's on your PATH) and click
   **Test binary** — you should see the version string.
2. Set the **default daemon** to `http://127.0.0.1:38081`, choose **Stagenet**, and click
   **Test daemon** — you should see the daemon's current height.
3. **Save**.

## 4. Create a wallet

1. On the create screen, keep **Create new wallet** selected and click **Generate new seed**.
2. Write the 25 words down, then either pass the three-word **verification** or **download**
   the backup. (The download is plaintext by design — treat it like cash.)
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

## 6. Automated integration tests (optional)

The `XaultWallet.IntegrationTests` project runs the same round-trips automatically. They are
skipped unless configured:

```bash
export XW_WALLET_RPC=/path/to/monero-wallet-rpc
export XW_DAEMON=http://127.0.0.1:38081
export XW_NETWORK=stagenet
dotnet test tests/XaultWallet.IntegrationTests
```

Without those variables the tests no-op (they print a skip notice), so a green run with no
node configured means "skipped", not "passed".

## What still stands between this and mainnet

Even after stagenet passes cleanly, this is **beta**. Before trusting real funds it needs a
**professional third-party security audit** and broader real-world testing. See `SECURITY.md`.
