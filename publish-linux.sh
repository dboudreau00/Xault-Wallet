#!/usr/bin/env bash
# Build a single-file, self-contained Linux release of XaultWallet. Requires the .NET 8 SDK.
set -euo pipefail
cd "$(dirname "$0")"
RID="linux-x64"
OUT="release/$RID"
echo "==> Cleaning previous release"; rm -rf "$OUT"
echo "==> Publishing ($RID, single-file, self-contained)"
dotnet publish src/XaultWallet.Desktop/XaultWallet.Desktop.csproj \
    -c Release -r "$RID" \
    -p:PublishSingleFile=true -p:SelfContained=true \
    -o "$OUT"
chmod +x "$OUT/XaultWallet" || true
echo; echo "Done: $OUT/XaultWallet"
