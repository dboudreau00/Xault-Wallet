# Building an installable XaultWallet release

This produces a **single self-contained `.exe`**: one file that runs on any Windows 10/11
(64-bit) machine with **no .NET installed**. It bundles the .NET runtime and all libraries.

You build this yourself on your own machine; it can't be produced without the .NET SDK.

---

## Windows (the common case)

### Prerequisites
- The **.NET 10 SDK** (you already have it if Visual Studio builds the project).
  Check in a terminal: `dotnet --version` should print `10.x`.

### One command
From the project root (the folder with `XaultWallet.sln`), in PowerShell:

```powershell
./publish-windows.ps1
```

Or run the raw command yourself:

```powershell
dotnet publish src/XaultWallet.Desktop/XaultWallet.Desktop.csproj `
    -c Release -r win-x64 -p:PublishSingleFile=true -p:SelfContained=true `
    -o release/win-x64
```

### Result
```
release/win-x64/XaultWallet.exe
```
That one file (~70–90 MB, since it contains the whole runtime) is your app. Copy it anywhere
and double-click. It carries the X icon and needs nothing else installed.

> First run may take a moment: a single-file self-extracting app unpacks to a temp folder once.

---

## The Windows installers

Both installers are in the repository: `installer/XaultWallet.iss` (Inno Setup 6, the
`setup.exe`) and `installer/XaultWallet.wxs` (WiX Toolset 3, the `.msi`). Each takes the published
exe and the four docs from `out/`: publish `win-x64` there, copy `README.md`, `SECURITY.md`,
`CHANGELOG.md` and `LICENSE` next to the exe, then run the command in the header of the installer
file (the `iscc` line is also in README's Build section). The release workflow builds and
test-installs both: a silent per-user install must put the exact published exe in place, and
uninstalling must remove it again.

---

## Other platforms

The project is cross-platform. On Linux:

```bash
./publish-linux.sh          # -> release/linux-x64/XaultWallet
```

On macOS, publish with `-r osx-x64` (Intel) or `-r osx-arm64` (Apple Silicon). Note macOS apps
from unidentified developers need a right-click → *Open* the first time, or proper code-signing
+ notarization for distribution.

---

## Signing (before you hand it to anyone else)

An unsigned `.exe` triggers SmartScreen ("Windows protected your PC") on other people's
machines. For personal use, click *More info → Run anyway*. To distribute without that warning,
sign the binary with a **code-signing certificate** (`signtool sign /fd SHA256 ...`). This
matters more for a wallet than for most apps, because users are trusting the binary with keys.

---

## IMPORTANT: this is still beta

A packaged `.exe` looks finished, but packaging changes nothing about the code's maturity:

- **Unaudited.** Get a professional security audit before this holds real mainnet funds.
- **Test networks first.** Exercise the full flow on stagenet before mainnet
  ([STAGENET-TESTING.md](STAGENET-TESTING.md)).
- **The bundled app does not include `monero-wallet-rpc`**, by design. Whoever runs it points
  Settings at their own verified `monero-wallet-rpc` (see [SETUP-MONERO-RPC.md](SETUP-MONERO-RPC.md)), or uses the in-app
  *Download & install*, which fetches the official build and checks Monero's signature and checksum
  first. The wallet never ships someone else's key-handling binary.
- **Back up your seed** independently of the app.

Version is stamped as `0.6.0a-beta` in the project file's `<ReleaseVersion>`, the name the tag,
the archives and Settings → About use. Beside it, `<Version>` (`0.6.0-beta.a`, SemVer) and
`<FileVersion>` (`0.6.0.1`, numeric, also the MSI's version) carry the same release in the forms
.NET and Windows need: bump all three together. A lower-case letter after the patch number marks a
revision of that release (0.6.0a, 0.6.0b, …); without one, `<ReleaseVersion>` and `<Version>` are
the same.

## Automated releases (GitHub Actions)

`.github/workflows/release.yml` runs the unit tests, then builds single-file self-contained
executables for `win-x64`, `linux-x64` and `osx-arm64`, plus two Windows installers:
`XaultWallet-<version>-win-x64-setup.exe` (Inno Setup, from `installer/XaultWallet.iss`) and
`XaultWallet-<version>-win-x64.msi` (WiX Toolset 3, from `installer/XaultWallet.wxs`). Each is
checked in the same job: a silent per-user install must put the exact published exe in place and
uninstalling must remove it again. Each archive holds the executable plus
`README.md`, `SECURITY.md`, `CHANGELOG.md` and `LICENSE`. They are attached with a `SHA256SUMS` file
to a **draft** release for you to review and publish. There are two ways to start it:

- **Push a tag** such as `v0.6.0a-beta`.
- **Run it by hand** (no git needed): GitHub → Actions → Release → *Run workflow*, with `version` set
  to `v0.6.0a-beta`. The draft targets the commit the run started from (normally `main`). GitHub
  creates the tag only when you press **Publish** on the draft: the workflow never creates or moves a
  tag. Run without a version, it only builds (a dry run) and publishes nothing.

Before building, the workflow stops if the version isn't `vX.Y.Z`, `vX.Y.Za` (a revision letter) or
either followed by `-suffix`, doesn't match `<ReleaseVersion>` in `XaultWallet.Desktop.csproj` (or
`<Version>` when there is no `<ReleaseVersion>`), has no `## <version>` section in `CHANGELOG.md`, or
already has a release (draft or published). After building, it runs the Windows end-to-end test
(`e2e-windows.yml`) on the very `win-x64` zip it built, and drafts nothing if that fails. The release
notes are a warning header plus that CHANGELOG section. A hyphenated version (`-beta`, `-rc.1`) is marked as a pre-release, so it never
shows as the repository's latest release. The binaries are unsigned until a code-signing certificate
is added to the workflow.

Publishing a draft creates its tag, which starts the workflow again; it sees the release already
exists and does nothing. While a draft exists, don't also push the tag yourself: the tag would point
wherever you pushed it, not necessarily at the commit that was built.

Checklist: the CHANGELOG section is dated, and CI is green on the commit being released.

`SelfContained` / `PublishSingleFile` apply only when publishing (`_IsPublishing`), so a plain
`dotnet publish -r <rid>` produces the single file, while ordinary builds stay framework-dependent.
