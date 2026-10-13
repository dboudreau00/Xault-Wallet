# The browser companion

A small extension for **Chrome** and **Firefox** that lets the browser hand payment requests to
XaultWallet and ask it for receive addresses. **The extension holds no key, no seed, no password, no
balance and no history**, and it stores nothing. XaultWallet on your computer does everything that
matters; the browser can only ask, and you confirm in the app.

What it does:

- **Pay from a page.** Its popup lists the `monero:` payment links and Monero addresses on the page
  you're on (looked for only when you open the popup). *Pay* opens that payment in XaultWallet's Send
  tab, with the page it came from. Or paste an address or link into the popup. Or select an address
  on any page, right-click, *Pay with XaultWallet*.
- **Get a receive address.** *New address* asks XaultWallet for a fresh subaddress of the wallet on
  screen, labelled with the site (or what you type), to paste into a form. XaultWallet shows the start
  and end of the same address in a notice, and lists it, labelled, on its Receive tab.
- **Status.** Whether XaultWallet is running, locked or unlocked, and the network of the wallet on
  screen (never its name or balance).

Nothing is ever sent from the browser. A payment request only fills XaultWallet's Send form; you
review it there (the confirmation says *Requested by your browser (shop.example)*) and press
*Send now* in the app, as for any payment.

## Install

1. **Connect the browser in XaultWallet.** Settings, *Browser companion*, *Connect* next to Chrome or
   Firefox. This writes a native messaging host manifest (and on Windows a registry key under
   `HKEY_CURRENT_USER`) naming this XaultWallet and the one extension allowed to use it. *Disconnect*
   removes it; uninstalling XaultWallet removes it too.
2. **Install the extension.** From a release: `XaultWallet-<version>-browser-chrome.zip` or
   `…-browser-firefox.zip` (checksums in `SHA256SUMS`). From source: `node extension/build.mjs`
   (Node 20 or later, no dependencies) writes `extension/dist/chrome` and `extension/dist/firefox`.
   - **Chrome** (and Chromium browsers): unzip, open `chrome://extensions`, turn on *Developer mode*,
     *Load unpacked*, choose the folder. Its id must read `kpcmnhbbahdifkhgibnboafelgfkgcdj`: that is
     the only Chrome extension XaultWallet answers.
   - **Firefox**: release Firefox only installs extensions Mozilla has signed. Until the companion is
     listed on addons.mozilla.org, load it from `about:debugging`, *This Firefox*, *Load Temporary
     Add-on* (it stays until Firefox restarts), or sign it for yourself as an unlisted add-on, or use
     Firefox Developer Edition or Nightly. Its id is `xaultwallet-companion@dboudreau.dev`.
3. **Optional: let XaultWallet open `monero:` links** (Windows and Linux): Settings, *Browser
   companion*, *Open them here*. A click on a `monero:` link in any browser then starts XaultWallet (or
   hands the link to the copy already running) with the payment waiting in the Send tab. This needs no
   extension at all.

Paths it writes: Windows `%LOCALAPPDATA%\XaultWallet\browser\dev.dboudreau.xaultwallet.chrome.json` and
`.firefox.json`, named by `HKCU\Software\Google\Chrome\NativeMessagingHosts\dev.dboudreau.xaultwallet`
and `HKCU\Software\Mozilla\NativeMessagingHosts\dev.dboudreau.xaultwallet`; `monero:` links under
`HKCU\Software\Classes\monero`. Linux `~/.config/google-chrome/NativeMessagingHosts/` and
`~/.mozilla/native-messaging-hosts/` (`dev.dboudreau.xaultwallet.json`), and for links
`~/.local/share/applications/xaultwallet-monero-link.desktop` plus a line in `~/.config/mimeapps.list`.
macOS `~/Library/Application Support/Google/Chrome/NativeMessagingHosts/` and
`~/Library/Application Support/Mozilla/NativeMessagingHosts/` (no `monero:` links on macOS: the release
is a plain executable, not an app bundle).

## How it fits together

```
 Chrome / Firefox                       your computer, your user only
┌─────────────────────┐ native     ┌──────────────────────┐  local pipe   ┌──────────────────────┐
│ companion extension │ messaging  │ XaultWallet.exe,      │  (Windows) or │ XaultWallet (running)│
│ popup, right-click  │──────────▶ │ started as the native │ ────────────▶ │ checks every request,│
│ no keys, no storage │ (stdin/out)│ host: no window, no   │  Unix socket  │ shows it, you confirm│
└─────────────────────┘            │ vault, passes messages│  (same user)  │ (vault, keys, sends) │
                                   └──────────────────────┘                └──────────────────────┘
```

- The browser starts XaultWallet's own executable as the **native messaging host** only for the
  extension the manifest names (Chrome checks `allowed_origins`, Firefox `allowed_extensions`). The
  host checks it again from the command line the browser gave it, and refuses anything else.
- The host carries one message at a time to the running app over a **local channel only your user
  account can open**: a named pipe on Windows, a Unix socket in your private runtime folder
  (`/run/user/<uid>`) or in XaultWallet's own `0700` data folder elsewhere. Both ends check that the
  other side runs as the same user.
- For *Pay* or *Open XaultWallet* while the app isn't running, the host starts it (and then passes the
  request on). Status and *New address* never start it.
- A second launch of XaultWallet (a `monero:` link Windows or Linux hands to it) uses the same channel to
  pass the link to the copy already running.

## Protocol, version 1

One JSON object per request and per reply. The browser frames each with its length (a 32-bit
little-endian integer, at most 64 KiB accepted); the local channel uses one line each (at most 16 KiB).
Every field is checked by the host and again by the app. Unknown fields are ignored; a field twice,
another version, an unknown type or a bad value is refused.

Requests:

| `type` | Fields | What happens |
|---|---|---|
| `status` | | The app's state. |
| `show` | | The window comes forward (the app starts if it isn't running). |
| `pay` | `uri` (a `monero:` link, at most 4096 characters), `origin` (optional: the page, `https://host[:port]`) | The link opens in the Send tab (after the next unlock if the vault is locked). The app starts if it isn't running. |
| `receive` | `label` (optional, at most 40 characters), `origin` (optional) | A new subaddress of the wallet on screen. |

Every request carries `"v": 1` and an `"id"` (1 to 64 letters, digits, `-` or `_`) that its reply repeats.

```json
{"v":1,"id":"7f3a9c21e0b44d58","type":"pay","uri":"monero:8B…?tx_amount=1.25&tx_description=Order%204471","origin":"https://shop.example"}
```

Replies are `{"v":1,"id":…,"ok":true,…}` with:

- `status`: `"app": "XaultWallet"`, `"version"`, `"state"` (`setup`, `locked` or `unlocked`); when
  unlocked also `"network"` (`Mainnet`, `Stagenet`, `Testnet` or `Regtest`), `"watchOnly"` and `"ready"`.
- `pay`: `"result"`: `shown`, `after-unlock` (it waits for the unlock) or `waiting` (it opens when
  Settings closes).
- `receive`: `"address"` and `"network"`.

or a refusal, `{"v":1,"id":…,"ok":false,"error":{"code":…,"message":…}}`, with one of these codes:
`bad-request`, `too-large`, `unsupported-version`, `unknown-type`, `not-running`, `no-vault`, `locked`,
`not-ready`, `busy` (a payment is being confirmed in the app), `too-many` (more than 6 payment
requests or 5 new addresses a minute), `failed`.

## Threat model

**The rule:** the browser is untrusted. It can ask, it can never decide, and it holds nothing worth
stealing.

**What it protects against**

- **Infostealers that copy browser data.** Wallet extensions that keep an encrypted vault in the
  browser profile get it stolen and cracked. This one keeps nothing: no storage permission, no
  `localStorage` or IndexedDB, no keys, no seed, no balance, no address book. A copied browser profile
  holds the extension's code and nothing else of yours.
- **Web pages.** A page can't talk to the extension: it has no content scripts, no
  `externally_connectable`, no web-accessible resources (so a page can't even detect it), and its pages
  forbid network access (`connect-src 'none'`). The popup reads the page only when you open it, and
  only to find payment links and addresses.
- **Other extensions.** The native host answers only the companion's id (Chrome checks the origin,
  Firefox the add-on id, and the host checks both again).
- **Other people on the computer.** The local channel is limited to your user account; a server or
  client running as anyone else is refused.
- **A compromised browser tab.** Browser content runs sandboxed; it can't open the channel, and
  nothing the app sends back is a secret.
- **An address swapped by something else.** A payment request is only ever a proposal in the Send
  tab, with where it came from shown on the form and in the confirmation. A request never replaces a
  payment being confirmed. A new receive address appears in the app too (a notice with its start and
  end, and labelled on the Receive tab), to compare with what the browser shows.
- **Being repointed.** At every start XaultWallet checks that each browser connection it made still
  leads to it. If another program rewrote one (to stand in for XaultWallet and, say, hand your browser
  its own "receive" address), you get a warning across the top of the window. The same goes for
  `monero:` links. On Windows the channel's name can't be taken first by another program without the
  app noticing and saying so.
- **Floods.** At most 6 payment requests and 5 new addresses a minute; anything bigger than a few KiB
  is refused before it is read.

**What it does not protect against**

- **Malware running as you that can do what you can.** It can read and write your files, log your
  keystrokes, read the memory of your programs (unless Enhanced Security is on, see below) and rewrite
  any of the files and registry keys above. No software wallet survives that; the companion doesn't
  make it worse (there is nothing in the browser to steal), and the tripwire makes the cheapest way to
  abuse the companion, repointing it, visible. Keep real funds where malware isn't.
- **A malicious update of the extension.** It still couldn't sign or send anything, but it could
  propose a payment to its own address or show you an address that isn't yours. Check what the app's
  confirmation says, and compare addresses with the app. Install the companion only from this
  repository's releases or its official store listing.
- **A Firefox add-on registered under the same id before the official listing exists.** Until the
  companion is published on addons.mozilla.org, someone else could publish an add-on with its id there.
  Install only from this repository. (The Chrome id comes from a key whose private half was never kept,
  so no store listing can ever claim it; a store listing would get an id of its own, added to the app in
  a release.)
- **Whoever sees your screen.** The popup shows the receive address it got.

## Enhanced Security

Settings, *Enhanced Security* (Windows, off by default) closes the ways common malware watches and
drives a desktop wallet, on the app itself: screenshots and recording show nothing where the window is,
only a real keyboard, mouse, touchscreen or pen can operate it (not injected input, and not hidden-VNC
remote control), other programs can't read its window through accessibility interfaces or its memory
(and its wallet backend's), and it won't start on a hidden desktop. Screen readers, voice control,
password-manager auto-type and remote-control tools can't operate XaultWallet while it is on. When you
turn it on, XaultWallet asks for one click: if your click doesn't get through (an unusual input
device), it turns itself off again. See [SECURITY.md](../SECURITY.md#enhanced-security).

## Developing

- `node --test extension/test/*.test.mjs`: the extension's unit tests (Node's own runner, no
  dependencies).
- `node extension/build.mjs [--zip <dir> --name <prefix>]`: both builds, and reproducible zips. The build
  stops if the extension ids it produces aren't the ones the app allows
  (`src/XaultWallet.Core/Companion/BrowserRegistration.cs`).
- The end-to-end runs exercise the real native host: the Linux headless run and the Windows run of the
  published exe ask for status, a receive address and a payment, and check what happens in the app's
  own screens.
