// Builds the XaultWallet companion for Chrome and Firefox from extension/src, with no dependencies.
//
//   node extension/build.mjs                    -> extension/dist/chrome and extension/dist/firefox
//   node extension/build.mjs --zip <dir> --name <prefix>
//                                               -> also <dir>/<prefix>-chrome.zip and <prefix>-firefox.zip
//
// The version comes from the desktop app (ReleaseVersion in XaultWallet.Desktop.csproj), so the
// extension and the app it talks to always ship together. The build stops if the extension ids it
// produces aren't the ones the app allows (BrowserRegistration.cs): a mismatch would ship an extension
// the app refuses. Zips are stored (not compressed) with fixed dates: the same sources give the same bytes.
import { createHash } from "node:crypto";
import { cpSync, mkdirSync, readFileSync, readdirSync, rmSync, statSync, writeFileSync } from "node:fs";
import { dirname, join, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const repo = join(here, "..");
const src = join(here, "src");
const dist = join(here, "dist");

const FIREFOX_ID = "xaultwallet-companion@dboudreau.dev";

function fail(message) {
  console.error("build: " + message);
  process.exit(1);
}

// "0.7.0-beta" -> manifest version "0.7.0"; "0.7.0a-beta" -> "0.7.0.1" (a = 1, b = 2, ...).
export function manifestVersion(release) {
  const m = /^(\d+)\.(\d+)\.(\d+)([a-z])?(?:-[0-9A-Za-z.-]+)?$/.exec(release);
  if (!m) {
    fail(`ReleaseVersion "${release}" isn't a version this build understands`);
  }

  const base = `${Number(m[1])}.${Number(m[2])}.${Number(m[3])}`;
  return m[4] ? `${base}.${m[4].charCodeAt(0) - 96}` : base;
}

// Chrome's id for an unpacked or self-hosted extension: the first 128 bits of SHA-256 of its public
// key, written with the letters a to p.
export function chromeIdFromKey(base64Key) {
  const digest = createHash("sha256").update(Buffer.from(base64Key, "base64")).digest();
  return Array.from(digest.subarray(0, 16), (b) => String.fromCharCode(97 + (b >> 4)) + String.fromCharCode(97 + (b & 15))).join("");
}

function releaseVersion() {
  const csproj = readFileSync(join(repo, "src", "XaultWallet.Desktop", "XaultWallet.Desktop.csproj"), "utf8");
  const m = /<ReleaseVersion>([^<]+)<\/ReleaseVersion>/.exec(csproj);
  if (!m) {
    fail("no <ReleaseVersion> in XaultWallet.Desktop.csproj");
  }

  return m[1].trim();
}

function allowedIds() {
  const cs = readFileSync(join(repo, "src", "XaultWallet.Core", "Companion", "BrowserRegistration.cs"), "utf8");
  const chrome = /ChromeExtensionIds = \[([^\]]*)\]/.exec(cs);
  const firefox = /FirefoxExtensionIds = \[([^\]]*)\]/.exec(cs);
  const ids = (m) => (m ? [...m[1].matchAll(/"([^"]+)"/g)].map((x) => x[1]) : []);
  return { chrome: ids(chrome), firefox: ids(firefox) };
}

function files(dir) {
  return readdirSync(dir).flatMap((name) => {
    const path = join(dir, name);
    return statSync(path).isDirectory() ? files(path) : [path];
  });
}

// ---- a minimal, deterministic zip writer (method 0, stored) ----

const CRC_TABLE = Array.from({ length: 256 }, (_, n) => {
  let c = n;
  for (let k = 0; k < 8; k++) {
    c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
  }
  return c >>> 0;
});

function crc32(buffer) {
  let c = 0xffffffff;
  for (const byte of buffer) {
    c = CRC_TABLE[(c ^ byte) & 0xff] ^ (c >>> 8);
  }
  return (c ^ 0xffffffff) >>> 0;
}

export function zip(root, out) {
  const entries = files(root).map((path) => ({ name: relative(root, path).split("\\").join("/"), data: readFileSync(path) }))
    .sort((a, b) => (a.name < b.name ? -1 : a.name > b.name ? 1 : 0));
  const DOS_DATE = 0x0021; // 1980-01-01, fixed so a rebuild gives the same bytes
  const parts = [];
  const central = [];
  let offset = 0;
  for (const e of entries) {
    const name = Buffer.from(e.name, "utf8");
    const crc = crc32(e.data);
    const local = Buffer.alloc(30);
    local.writeUInt32LE(0x04034b50, 0);
    local.writeUInt16LE(20, 4);
    local.writeUInt16LE(0x0800, 6); // UTF-8 names
    local.writeUInt16LE(0, 8);
    local.writeUInt16LE(0, 10);
    local.writeUInt16LE(DOS_DATE, 12);
    local.writeUInt32LE(crc, 14);
    local.writeUInt32LE(e.data.length, 18);
    local.writeUInt32LE(e.data.length, 22);
    local.writeUInt16LE(name.length, 26);
    local.writeUInt16LE(0, 28);
    parts.push(local, name, e.data);

    const header = Buffer.alloc(46);
    header.writeUInt32LE(0x02014b50, 0);
    header.writeUInt16LE(20, 4);
    header.writeUInt16LE(20, 6);
    header.writeUInt16LE(0x0800, 8);
    header.writeUInt16LE(0, 10);
    header.writeUInt16LE(0, 12);
    header.writeUInt16LE(DOS_DATE, 14);
    header.writeUInt32LE(crc, 16);
    header.writeUInt32LE(e.data.length, 20);
    header.writeUInt32LE(e.data.length, 24);
    header.writeUInt16LE(name.length, 28);
    header.writeUInt32LE(offset, 42);
    central.push(header, name);
    offset += local.length + name.length + e.data.length;
  }

  const centralBytes = Buffer.concat(central);
  const end = Buffer.alloc(22);
  end.writeUInt32LE(0x06054b50, 0);
  end.writeUInt16LE(entries.length, 8);
  end.writeUInt16LE(entries.length, 10);
  end.writeUInt32LE(centralBytes.length, 12);
  end.writeUInt32LE(offset, 16);
  writeFileSync(out, Buffer.concat([...parts, centralBytes, end]));
}

// ---- the two builds ----

export function build() {
  const release = releaseVersion();
  const version = manifestVersion(release);
  const base = JSON.parse(readFileSync(join(src, "manifest.json"), "utf8"));
  const allowed = allowedIds();

  const chromeId = chromeIdFromKey(base.key);
  if (!allowed.chrome.includes(chromeId)) {
    fail(`the manifest's key gives the Chrome id ${chromeId}, which BrowserRegistration.cs doesn't allow (${allowed.chrome.join(", ")})`);
  }

  if (!allowed.firefox.includes(FIREFOX_ID)) {
    fail(`the Firefox id ${FIREFOX_ID} isn't in BrowserRegistration.cs (${allowed.firefox.join(", ")})`);
  }

  const chrome = { ...base, version, version_name: release };
  const firefox = { ...base, version };
  delete firefox.key;
  delete firefox.minimum_chrome_version;
  firefox.background = { scripts: ["shared.js", "background.js"] };
  firefox.browser_specific_settings = {
    gecko: {
      id: FIREFOX_ID,
      strict_min_version: "128.0",
      data_collection_permissions: { required: ["none"] },
    },
  };

  rmSync(dist, { recursive: true, force: true });
  for (const [name, manifest] of [["chrome", chrome], ["firefox", firefox]]) {
    const out = join(dist, name);
    mkdirSync(out, { recursive: true });
    cpSync(src, out, { recursive: true, filter: (path) => !path.endsWith("manifest.json") });
    writeFileSync(join(out, "manifest.json"), JSON.stringify(manifest, null, 2) + "\n");
  }

  return { release, version, chromeId };
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const result = build();
  const zipAt = process.argv.indexOf("--zip");
  if (zipAt > 0) {
    const dir = process.argv[zipAt + 1];
    const nameAt = process.argv.indexOf("--name");
    const prefix = nameAt > 0 ? process.argv[nameAt + 1] : `XaultWallet-companion-${result.release}`;
    if (!dir || !prefix || /[\\/]/.test(prefix)) {
      fail("usage: --zip <dir> [--name <file prefix>]");
    }

    mkdirSync(dir, { recursive: true });
    for (const name of ["chrome", "firefox"]) {
      const out = join(dir, `${prefix}-${name}.zip`);
      zip(join(dist, name), out);
      console.log("wrote " + out);
    }
  }

  console.log(`XaultWallet companion ${result.release} (manifest ${result.version}); Chrome id ${result.chromeId}, Firefox id ${FIREFOX_ID}`);
}
