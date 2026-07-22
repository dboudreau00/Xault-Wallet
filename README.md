# XaultWallet

<table>
  <tr>
    <td align="center">
      <b>Before</b><br>
      <img src="https://github.com/user-attachments/assets/117e91aa-bcc5-4ce7-b7e4-81b3d92138eb" width="400" alt="First Screenshot">
    </td>
    <td align="center">
      <b>After</b><br>
      <img src="https://github.com/user-attachments/assets/88a4349c-f2da-4287-99ef-409e035acd33" width="400" alt="Second Screenshot">
    </td>
    <td align="center">
      <b>After</b><br>
      <img src="https://github.com/user-attachments/assets/004956c0-71fe-4f8d-a8ed-c9422a83fecc" width="400" alt="Third Screenshot">
    </td>
  </tr>
</table>



## Why this design

The single most dangerous thing a wallet author can do is hand-roll Monero's cryptography
(ring signatures, RingCT, bulletproofs, stealth addresses). This project deliberately does
**not** do that. Instead it drives the official **`monero-wallet-rpc`** binary — the same
crypto that Monero's own tools use — and focuses its own code on the part where correctness
is achievable and verifiable: the encrypted vault, the KDF, and the duress logic.


> ## ⚠️ Unaudited beta — do NOT use with real funds
>
> This is an **educational, work-in-progress** Monero wallet. It has **not** had a professional
> security audit. It may contain bugs that cause **permanent, irreversible loss of funds** —
> Monero transactions cannot be reversed or refunded.
>
> - **Do not store real (mainnet) XMR in it.** Use **testnet** or **stagenet** only.
> - Provided **as-is, with no warranty of any kind** (see [LICENSE](LICENSE)).
> - The **"wipe real wallet on duress"** option is irreversible and can destroy your seed.
> - Always keep an **independent offline backup** of your 25-word seed.
>
> If you are looking for a wallet to actually hold Monero, use an established, audited one
> (e.g. the official Monero GUI/CLI, Feather, or Cake). Read [SECURITY.md](SECURITY.md) in full
> before doing anything with this project.

A desktop Monero (XMR) wallet, structured after **Wasabi Wallet** (.NET + Avalonia +
MVVM). It is **password-protected**, **encrypted at rest with AES-256-GCM**, and supports a
**duress password** that opens a decoy wallet (and can optionally wipe the real one).

> This is a solid, reviewable foundation with a fully-implemented, unit-tested security core —
> but it has **not** been independently audited. Any wallet that holds meaningful money should be.



## Architecture

```
XaultWallet.Core            ← no UI; unit-testable
├── Security/
│   ├── SecureBuffer      pinned, zeroed memory for secrets
│   ├── VaultCrypto       Argon2id KDF + AES-256-GCM (authenticated)
│   ├── VaultFile         on-disk format: 2 equal-size, indistinguishable slots
│   ├── VaultManager      create / unlock / change-password / duress policy
│   └── PasswordStrength  conservative entropy estimate
├── Models/               WalletSecrets, ProfileKind (Real | Duress), network, …
└── Monero/
    ├── MoneroRpcClient        JSON-RPC 2.0 over HTTP digest auth
    ├── MoneroProcessManager   launches monero-wallet-rpc on a random localhost port
    └── MoneroWalletService    balance / send / receive / history

XaultWallet.Desktop         ← Avalonia 11 app (MVVM via CommunityToolkit.Mvvm)
├── unlock / create-wallet / dashboard views
└── the UI is IDENTICAL whether the real or the duress wallet is opened

XaultWallet.Core.Tests      ← xUnit tests for the crypto + duress behaviour
```

### How the duress password works

The vault file always contains exactly **two equal-sized encrypted slots**. One holds the
real wallet; the other holds either the decoy wallet or — if you never set a duress
password — uniform random bytes that are indistinguishable from an encrypted slot. When you
type a password, every slot is tried; whichever slot's AES-GCM authentication tag verifies
is the one that unlocks. There is **no plaintext password comparison anywhere**, and slot
position is randomised, so an attacker who seizes the file cannot prove a hidden wallet
exists. `ProfileKind` (Real vs Duress) lives *inside* the encrypted payload, so it is only
visible after a correct password decrypts a slot.

Two duress policies are configurable at creation time:
- **Decoy** (default): silently open the second wallet. Safest for your funds.
- **Wipe**: additionally overwrite the real slot so it can't be recovered from this device.

## Prerequisites

1. **.NET 8 SDK** — <https://dotnet.microsoft.com/download>
2. **Official Monero CLI tools** — download `monero-wallet-rpc` from
   <https://www.getmonero.org/downloads/> and either place it next to the built app or set
   its path in `AppServices.WalletRpcBinaryPath`.
3. A Monero daemon to sync against — a local `monerod` (default
   `http://127.0.0.1:18081`) or a remote/public node.

## Build & run

The project targets **.NET 8**. With the .NET 8 SDK installed, one command builds and tests everything:

```bash
./build.sh            # Linux/macOS   (restore + build Release + unit tests)
./build.ps1           # Windows PowerShell
```

Or run the steps manually:

```bash
dotnet restore
dotnet test                                     # security-core + hardening unit tests
dotnet run --project src/XaultWallet.Desktop    # launches the wallet
```

### Visual Studio 2022

1. **Requirements:** Visual Studio 2022 **17.8 or later** with the **.NET desktop development**
   workload (this includes the .NET 8 SDK). Nothing else needs installing to build.
2. Extract the download and double-click **`XaultWallet.sln`**.
3. `XaultWallet.Desktop` is listed first in the solution, so it is the default startup
   project — press **F5** (debug) or **Ctrl+F5** (run). The first build restores NuGet
   packages (Avalonia, CommunityToolkit.Mvvm, Konscious.Argon2), so it needs internet once.
4. Run the unit tests from **Test Explorer** (Test ▸ Run All Tests). The integration tests
   are skipped unless the `XW_WALLET_RPC` / `XW_DAEMON` environment variables are set — see
   `STAGENET-TESTING.md`.

At runtime the wallet drives the external `monero-wallet-rpc` binary from the official
Monero CLI tools — it is not compiled into this solution. Launch the app once, open
**Settings**, and point it at that binary (the **Test binary** button confirms it works).

On first launch, open **Settings** (top-right) to point XaultWallet at your
`monero-wallet-rpc` binary and daemon, and use the **Test binary** / **Test daemon** buttons
to confirm both work before creating a wallet. Settings are saved to
`%APPDATA%/XaultWallet/settings.json` (non-secret: just the binary path, default daemon,
network, and refresh interval).

For a full end-to-end walkthrough on Monero's test network, see
[`STAGENET-TESTING.md`](STAGENET-TESTING.md). Automated integration tests live in
`tests/XaultWallet.IntegrationTests` and are skipped unless `XW_WALLET_RPC` and `XW_DAEMON`
are set, so the default `dotnet test` stays green without a node.

The vault file is written to `%APPDATA%/XaultWallet/vault.xv` (Windows) or the platform
equivalent. It is the **only** thing that persists on disk — the Monero wallet files
themselves are restored from your seed into an ephemeral temp directory on unlock and
shredded on lock.

## What is complete vs. what needs work

**Complete and unit-tested:** the vault format, Argon2id + AES-256-GCM, the dual-slot
duress mechanism (decoy + wipe), password change, atomic file writes.

**Seed generation & backup:** new 25-word Monero seeds are generated by the official
`monero-wallet-rpc` (`create_wallet` + `query_key`) in a throwaway instance — never
hand-rolled. The create flow forces a backup step: you must either verify three random
words or download a plaintext backup file (with a prominent warning) before the vault is
sealed. Importing an existing seed is also supported, for both the real and decoy wallets.

**Implemented but needs integration testing against a live daemon:** the RPC client,
process manager, and send/receive/history flows. These depend on the `monero-wallet-rpc`
binary being present.

**Intentionally out of scope for this foundation:** hardware-wallet support, multisig,
a bundled node, and a professional security audit.

## Robustness & error handling

This build has had a dedicated hardening pass. Notable behaviour:

- **Process supervision:** monero-wallet-rpc is launched with random localhost port +
  credentials; both stdout and stderr are drained (so the child can't block on a full pipe);
  early exits are detected and surfaced with the captured stderr tail; and the child process
  and its ephemeral temp dir are always cleaned up on failure, cancellation, or app close.
- **Readiness** is probed with `get_version`, which responds with or without an open wallet,
  so neither restore nor generation hangs waiting on the wrong signal.
- **Imported seeds are validated** (they must actually open a wallet) *before* they're sealed
  into the vault, so a typo can't produce an unopenable vault. If the RPC binary is missing,
  it falls back to a word-count check and tells you validation was skipped.
- **The dashboard** never blocks on a synchronous refresh: it opens the wallet, then polls
  balance/height/history on a background timer. A manual refresh is bounded by a timeout.
  Startup failures show a Retry button instead of a dead screen.
- **Vault integrity:** the file format is length- and header-checked, KDF parameters are
  bounds-checked on load (a tampered header can't force a multi-GB allocation), saves are
  written to a temp file, flushed to disk, then atomically swapped in.
- **Graceful shutdown** intercepts window close, tears the wallet down (killing the child and
  shredding temp files), then exits. Unhandled/background exceptions are logged, not fatal.
- **Logging** goes to `%APPDATA%/XaultWallet/logs/` and deliberately never records seeds,
  passwords, keys, or RPC credentials.

## Beta status — read before trusting real funds

This is at a **beta-on-stagenet** bar, **not** a "trust it with savings" bar. It was written
carefully but, being a wallet, still needs the following before mainnet use:

1. A clean `dotnet restore && dotnet test` and manual compile on your machine (the author's
   environment could not compile it).
2. End-to-end testing against **stagenet**: generate a wallet, receive, send, restart, restore,
   and exercise the duress password — with a real `monero-wallet-rpc` and daemon.
3. A **professional third-party security audit**. Do not skip this for a wallet.

The downloadable seed backup is intentionally plaintext (that's what a backup is); the in-app
warning says so. See `SECURITY.md` for the threat model and the honest limits of the
memory-hygiene and plausible-deniability guarantees.


## Contributing & security

This is a personal, educational project shared in the open. Review, issues, and pull requests
are welcome — extra eyes on a self-custody wallet are exactly the point of open-sourcing it.
For anything security-sensitive, please follow the private reporting process in
[SECURITY.md](SECURITY.md) rather than opening a public issue.

## License

Released under the **MIT License** — see [LICENSE](LICENSE). Provided as-is, with no warranty.

> Prefer copyleft? MIT was chosen here mainly so the license text could be included exactly and
> correctly. If you'd rather use **GPLv3** (which better matches Monero's own ethos and keeps
> derivatives open source), swap it on GitHub: **Add file → Create new file →** name it
> `LICENSE` → **Choose a license template → GNU GPLv3**. GitHub inserts the canonical, verbatim
> license text for you. Update this section to match if you do.

## Acknowledgements

Built on the official Monero tools (`monerod`, `monero-wallet-rpc`) — this project drives them
rather than reimplementing Monero's cryptography. UI built with [Avalonia](https://avaloniaui.net/).
Brought to life in harmony — https://dboudreau.dev
