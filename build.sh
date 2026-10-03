#!/usr/bin/env bash
# One-shot restore + build + unit tests. Requires the .NET 8 SDK.
set -euo pipefail
cd "$(dirname "$0")"

echo "==> dotnet --version"
dotnet --version

echo "==> Restoring packages"
dotnet restore XaultWallet.sln

echo "==> Building (Release, warnings are errors)"
dotnet build XaultWallet.sln -c Release --no-restore

echo "==> Unit tests (reflection runner: fails on any failure or on zero discovered tests)"
dotnet run -c Release --no-build --project tools/TestRunner

echo
echo "Build OK. Launch the app with:"
echo "  dotnet run --project src/XaultWallet.Desktop -c Release"
echo
echo "Optional checks:"
echo "  # Every screen renders with zero binding errors; the Receive QR decodes to the shown address"
echo "  # (needs zbarimg for the QR check):"
echo "  dotnet run -c Release --project tools/UiSnapshots -- ui-snapshots"
echo "  # Real monero-wallet-rpc against a private regtest chain (see STAGENET-TESTING.md):"
echo "  XW_WALLET_RPC=/path/to/monero-wallet-rpc XW_DAEMON=http://127.0.0.1:18081 XW_NETWORK=regtest \\"
echo "    dotnet test tests/XaultWallet.IntegrationTests -c Release"
