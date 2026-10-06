# One-shot restore + build + unit tests. Requires the .NET 10 SDK.
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

Write-Host "==> dotnet --version"
dotnet --version

Write-Host "==> Restoring packages"
dotnet restore XaultWallet.sln

Write-Host "==> Building (Release, warnings are errors)"
dotnet build XaultWallet.sln -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

Write-Host "==> Running unit tests (security core + hardening)"
# Via the reflection runner, not `dotnet test`: environments like Windows Smart App
# Control block the VSTest host, making `dotnet test` "pass" with ZERO tests discovered.
# The runner compiles the test sources in directly and exits non-zero on any failure.
dotnet run -c Release --no-build --project tools/TestRunner
if ($LASTEXITCODE -ne 0) { throw "Unit tests failed." }

Write-Host ""
Write-Host "Build OK. Launch the app with:"
Write-Host "  dotnet run --project src/XaultWallet.Desktop -c Release"
Write-Host ""
Write-Host "Optional: render every screen headlessly (fails on binding errors):"
Write-Host "  dotnet run -c Release --project tools/UiSnapshots -- ui-snapshots"
