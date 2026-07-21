# Build a single-file, self-contained Windows release of XaultWallet.
# Requires the .NET 8 SDK. Run from the project root:  ./publish-windows.ps1
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$rid = "win-x64"
$out = "release/$rid"

Write-Host "==> Cleaning previous release"
if (Test-Path $out) { Remove-Item -Recurse -Force $out }

Write-Host "==> Publishing ($rid, single-file, self-contained)"
dotnet publish src/XaultWallet.Desktop/XaultWallet.Desktop.csproj `
    -c Release -r $rid `
    -p:PublishSingleFile=true `
    -p:SelfContained=true `
    -o $out

Write-Host ""
Write-Host "Done. Your app is here:"
Write-Host "  $((Resolve-Path "$out/XaultWallet.exe").Path)"
Write-Host ""
Write-Host "That single .exe runs on any Windows 10/11 x64 machine with NO .NET installed."
