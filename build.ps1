# One-shot restore + build + unit tests. Requires the .NET 8 SDK.
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

Write-Host "==> dotnet --version"
dotnet --version

Write-Host "==> Restoring packages"
dotnet restore XaultWallet.sln

Write-Host "==> Building (Release)"
dotnet build XaultWallet.sln -c Release --no-restore

Write-Host "==> Running unit tests (security core + hardening)"
dotnet test tests/XaultWallet.Core.Tests/XaultWallet.Core.Tests.csproj -c Release --no-build

Write-Host ""
Write-Host "Build OK. Launch the app with:"
Write-Host "  dotnet run --project src/XaultWallet.Desktop -c Release"
