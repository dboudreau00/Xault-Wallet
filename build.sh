#!/usr/bin/env bash
# One-shot restore + build + unit tests. Requires the .NET 8 SDK.
set -euo pipefail
cd "$(dirname "$0")"

echo "==> dotnet --version"
dotnet --version

echo "==> Restoring packages"
dotnet restore XaultWallet.sln

echo "==> Building (Release)"
dotnet build XaultWallet.sln -c Release --no-restore

echo "==> Running unit tests (security core + hardening)"
dotnet test tests/XaultWallet.Core.Tests/XaultWallet.Core.Tests.csproj -c Release --no-build

echo
echo "Build OK. Launch the app with:"
echo "  dotnet run --project src/XaultWallet.Desktop -c Release"
echo
echo "Integration tests (need a real monero-wallet-rpc + daemon) are opt-in:"
echo "  XW_WALLET_RPC=/path/to/monero-wallet-rpc XW_DAEMON=http://127.0.0.1:38081 \\"
echo "    dotnet test tests/XaultWallet.IntegrationTests/XaultWallet.IntegrationTests.csproj"
