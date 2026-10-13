# XaultWallet Companion (browser extension)

The Chrome and Firefox extension that hands payment requests to XaultWallet and asks it for receive
addresses. It holds no keys and stores nothing. What it does, how to install it, its protocol and its
threat model: [docs/BROWSER-COMPANION.md](../docs/BROWSER-COMPANION.md).

```
src/        the extension (Manifest V3, plain JavaScript, no dependencies, no build step needed to read it)
build.mjs   writes dist/chrome and dist/firefox (and, with --zip, reproducible zips)
test/       unit tests: node --test extension/test/*.test.mjs
```

`node extension/build.mjs` needs Node 20 or later. The manifest's version comes from the desktop app's
`ReleaseVersion`, and the build stops if the extension ids it produces aren't the ones the app allows.
