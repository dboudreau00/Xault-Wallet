// The companion's own logic, with Node's built-in test runner (no dependencies): node --test extension/test
import assert from "node:assert/strict";
import { createRequire } from "node:module";
import { readFileSync, readdirSync, mkdtempSync, rmSync, existsSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";
import { test } from "node:test";
import { build, chromeIdFromKey, manifestVersion, zip } from "../build.mjs";

const require = createRequire(import.meta.url);
const shared = require("../src/shared.js");
const here = dirname(fileURLToPath(import.meta.url));

const MAIN = "44AFFq5kSiGBoZ4NMDwYtN18obc8AemS33DBLWs3H7otXft3XjrpDtQGv7SqSsaBYBb98uNbr2VBBEt7f2wfn3RVGQBEP3A";
const STAGENET = "5" + MAIN.slice(1);
const SUB_MAIN = "8" + MAIN.slice(1);
const SUB_STAGENET = "7" + MAIN.slice(1);
const RLO = String.fromCodePoint(0x202e);
const ZWSP = String.fromCodePoint(0x200b);

test("addresses: shape only, every network, standard and integrated", () => {
  assert.ok(shared.isAddress(MAIN));
  assert.ok(shared.isAddress(STAGENET));
  assert.ok(shared.isAddress(MAIN + "1".repeat(11)));
  assert.ok(!shared.isAddress(MAIN.slice(1)));
  assert.ok(!shared.isAddress("3" + MAIN.slice(1)));
  assert.ok(!shared.isAddress(MAIN.slice(0, -1) + "0")); // 0 isn't base58
  assert.ok(!shared.isAddress(null));
});

test("monero: links are read the way the app reads them", () => {
  const link = `monero:${MAIN}?tx_amount=1.25&tx_description=Rent%20July&recipient_name=Alice`;
  assert.deepEqual(shared.parseUri(link), { address: MAIN, amount: "1.25", description: "Rent July", recipient: "Alice" });
  assert.deepEqual(shared.parseUri(`MONERO://${MAIN}`), { address: MAIN, amount: "", description: "", recipient: "" });
  assert.equal(shared.parseUri(`monero:${MAIN}?tx_amount=1,5`), null); // ambiguous: refused, as in the app
  assert.equal(shared.parseUri(`monero:${MAIN}?tx_amount=%ZZ`), null);
  assert.equal(shared.parseUri(`monero:${MAIN}?tx_description=a\nb`), null);
  assert.equal(shared.parseUri(`monero:${MAIN}?tx_description=a${RLO}b`), null); // refused raw, as in the app
  assert.equal(shared.parseUri(`monero:${MAIN}?tx_description=a${ZWSP}b`), null);
  assert.equal(shared.parseUri("monero:notanaddress"), null);
  assert.equal(shared.parseUri("bitcoin:1abc"), null);
});

test("what a link's note shows: no line breaks, no reversed text, a sensible length", () => {
  const parsed = shared.parseUri(`monero:${MAIN}?tx_description=Rent%0AOpened%20from%20your%20bank%E2%80%AE&recipient_name=${"n".repeat(500)}`);
  assert.equal(parsed.description, "Rent Opened from your bank");
  assert.equal(parsed.recipient.length, 100);
  assert.ok(parsed.recipient.endsWith("..."));
  const emoji = String.fromCodePoint(0x1f600);
  assert.equal(shared.fit(emoji.repeat(30), 40), emoji.repeat(20)); // never half a character
  assert.equal(shared.fit("a" + emoji, 2), "a");
});

test("what can be paid: a link, a bare address, or text with exactly one address", () => {
  assert.equal(shared.toPayable(` ${MAIN} `), "monero:" + MAIN);
  assert.equal(shared.toPayable(`monero:${MAIN}?tx_amount=2`), `monero:${MAIN}?tx_amount=2`);
  assert.equal(shared.toPayable(`Please send to ${MAIN} before Friday.`), "monero:" + MAIN);
  assert.equal(shared.toPayable(`${MAIN} or ${STAGENET}`), null);
  assert.equal(shared.toPayable("hello"), null);
  assert.equal(shared.toPayable(`x${MAIN}`), null); // part of a longer token isn't an address
  assert.equal(shared.toPayable(`Pay to:\n${MAIN}`), "monero:" + MAIN); // a line break in a selection is fine
  assert.equal(shared.toPayable("word ".repeat(80) + MAIN), null); // a whole page selected is too much to trust
});

test("plain http pages off this computer are flagged", () => {
  assert.ok(shared.isInsecurePage("http://shop.example/pay"));
  assert.ok(!shared.isInsecurePage("https://shop.example/pay"));
  assert.ok(!shared.isInsecurePage("http://localhost:3000/"));
  assert.ok(!shared.isInsecurePage("http://[::1]:3000/"));
});

test("origins: only web pages, reduced to scheme and host", () => {
  assert.equal(shared.originOf("https://shop.example/checkout?id=7#x"), "https://shop.example");
  assert.equal(shared.originOf("http://localhost:8080/a"), "http://localhost:8080");
  assert.equal(shared.originOf("chrome://extensions"), "");
  assert.equal(shared.originOf("file:///C:/x.html"), "");
  assert.equal(shared.originOf("not a url"), "");
  assert.equal(shared.hostOf("https://Shop.Example/x"), "shop.example");
});

test("replies: only XaultWallet's answer to this request, and only the fields of its kind", () => {
  assert.deepEqual(shared.checkReply({ v: 1, id: "a", ok: true, state: "locked", balance: 12 }, "a", "status"),
    { ok: true, state: "locked", network: "", watchOnly: false, ready: false, version: "" });
  assert.equal(shared.checkReply({ v: 1, id: "a", ok: true, state: "unlocked", network: "Stagenet", ready: true, version: "0.7.0-beta" }, "a", "status").version, "0.7.0-beta");
  assert.equal(shared.checkReply({ v: 1, id: "a", ok: true, state: "unlocked", network: "x".repeat(500) }, "a", "status").error.code, "bad-reply");
  assert.equal(shared.checkReply({ v: 1, id: "a", ok: true, state: "open" }, "a", "status").error.code, "bad-reply");
  assert.equal(shared.checkReply({ v: 1, id: "b", ok: true }, "a", "show").error.code, "bad-reply");
  assert.equal(shared.checkReply({ v: 2, id: "a", ok: true }, "a", "show").error.code, "bad-reply");
  assert.equal(shared.checkReply(null, "a", "show").error.code, "bad-reply");

  assert.equal(shared.checkReply({ v: 1, id: "a", ok: true, result: "constructor" }, "a", "pay").error.code, "bad-reply");
  assert.equal(shared.checkReply({ v: 1, id: "a", ok: true, result: "waiting" }, "a", "pay").result, "waiting");
  assert.equal(shared.checkReply({ v: 1, id: "a", ok: true, result: "held" }, "a", "pay").result, "held");

  assert.equal(shared.checkReply({ v: 1, id: "a", ok: true }, "a", "receive").error.code, "bad-reply");
  assert.equal(shared.checkReply({ v: 1, id: "a", ok: true, address: "nope", network: "Mainnet" }, "a", "receive").error.code, "bad-reply");
  assert.equal(shared.checkReply({ v: 1, id: "a", ok: true, address: SUB_MAIN, network: "Stagenet" }, "a", "receive").error.code, "bad-reply");
  assert.deepEqual(shared.checkReply({ v: 1, id: "a", ok: true, address: SUB_STAGENET, network: "Stagenet", x: 1 }, "a", "receive"),
    { ok: true, address: SUB_STAGENET, network: "Stagenet" });

  const notConnected = shared.checkReply({ v: 1, id: "a", ok: false, error: { code: "not-connected", message: "Connect it in Settings." } }, "a", "status");
  assert.equal(notConnected.error.code, "not-connected");
  const refused = shared.checkReply({ v: 1, id: "a", ok: false, error: { code: "locked", message: "Unlock XaultWallet first." } }, "a", "receive");
  assert.deepEqual(refused, { ok: false, error: { code: "locked", message: "Unlock XaultWallet first." } });
  const odd = shared.checkReply({ v: 1, id: "a", ok: false, error: { code: "pwned", message: "Line one\n" + RLO + "two" } }, "a", "pay");
  assert.deepEqual(odd, { ok: false, error: { code: "failed", message: "Line one two" } });
});

test("the browsers' native messaging errors become something to do", () => {
  assert.equal(shared.hostError(new Error("Specified native messaging host not found.")).code, "not-connected");
  assert.equal(shared.hostError(new Error("No such native application dev.dboudreau.xaultwallet")).code, "not-connected");
  assert.equal(shared.hostError(new Error("Access to the specified native messaging host is forbidden.")).code, "not-allowed");
  assert.equal(shared.hostError(new Error("Native host has exited.")).code, "host-error");
});

test("ask() sends one versioned request to the XaultWallet host and checks the reply", async () => {
  const sent = [];
  globalThis.chrome = {
    runtime: {
      sendNativeMessage: async (host, message) => {
        sent.push([host, message]);
        return { v: 1, id: message.id, ok: true, state: "unlocked", network: "Stagenet" };
      },
    },
  };
  try {
    const reply = await shared.ask({ type: "status" });
    assert.equal(reply.state, "unlocked");
    assert.equal(reply.network, "Stagenet");
    assert.equal(sent.length, 1);
    assert.equal(sent[0][0], "dev.dboudreau.xaultwallet");
    assert.equal(sent[0][1].v, 1);
    assert.match(sent[0][1].id, /^[0-9a-f]{16}$/);
  } finally {
    delete globalThis.chrome;
  }
});

test("ask() gives up in time when XaultWallet never answers", async () => {
  globalThis.chrome = { runtime: { sendNativeMessage: () => new Promise(() => {}) } };
  try {
    const reply = await shared.ask({ type: "status" }, 50);
    assert.equal(reply.error.code, "timeout");
  } finally {
    delete globalThis.chrome;
  }
});

test("the page scan finds what a person can see, nothing else", () => {
  // A tiny stand-in for the page: scanPage only reads.
  const box = (rect, visible = true) => ({ getBoundingClientRect: () => rect, checkVisibility: () => visible });
  const seen = { width: 300, height: 16, right: 300, bottom: 16 };
  const shown = box(seen);
  const transparent = box(seen, false);
  const offscreen = box({ width: 300, height: 16, right: -9000, bottom: 16 });
  const tiny = box({ width: 1, height: 1, right: 1, bottom: 1 });
  const link = `monero:${MAIN}?tx_amount=3`;
  const planted = `monero:${STAGENET}?recipient_name=Shop`;
  const other = (n) => "4" + MAIN.slice(1, -1) + n;
  globalThis.document = {
    querySelectorAll: (selector) => selector === "a[href]"
      ? [{ ...shown, getAttribute: () => link }, { ...shown, getAttribute: () => "https://example.com" }, { ...transparent, getAttribute: () => planted }, { ...shown, getAttribute: () => link }]
      : [
        { ...shown, getAttribute: () => "text", value: `pay ${STAGENET} now` },
        { ...shown, getAttribute: () => "hidden", value: other("A") },
        { ...shown, getAttribute: () => "password", value: other("B") },
        { ...offscreen, getAttribute: () => null, value: other("C") },
      ],
    body: {},
    createTreeWalker: () => {
      const nodes = [
        { nodeValue: `Our address: ${MAIN}. Thanks!`, parentElement: shown },
        { nodeValue: `Hidden: ${other("D")}`, parentElement: transparent },
        { nodeValue: `Screen readers only: ${other("E")}`, parentElement: tiny },
      ];
      let i = 0;
      return { nextNode: () => nodes[i++] || null };
    },
    createRange: () => ({ selectNodeContents() {}, getBoundingClientRect: () => seen }),
  };
  try {
    const found = shared.scanPage();
    assert.deepEqual(found.links, [link]);
    assert.deepEqual(found.addresses.sort(), [MAIN, STAGENET].sort());
  } finally {
    delete globalThis.document;
  }
});

test("versions follow the app's release, revision letters included", () => {
  assert.equal(manifestVersion("0.7.0-beta"), "0.7.0");
  assert.equal(manifestVersion("0.7.0a-beta"), "0.7.0.1");
  assert.equal(manifestVersion("1.2.3b"), "1.2.3.2");
});

test("the build: two manifests, our ids only, the app's version, no storage and no host access", () => {
  const result = build();
  const dist = join(here, "..", "dist");
  const chrome = JSON.parse(readFileSync(join(dist, "chrome", "manifest.json"), "utf8"));
  const firefox = JSON.parse(readFileSync(join(dist, "firefox", "manifest.json"), "utf8"));

  assert.equal(chromeIdFromKey(chrome.key), "kpcmnhbbahdifkhgibnboafelgfkgcdj");
  assert.equal(result.chromeId, "kpcmnhbbahdifkhgibnboafelgfkgcdj");
  assert.equal(chrome.background.service_worker, "background.js");
  assert.equal(firefox.key, undefined);
  assert.deepEqual(firefox.background, { scripts: ["shared.js", "background.js"] });
  assert.equal(firefox.browser_specific_settings.gecko.id, "xaultwallet-companion@dboudreau.dev");
  assert.equal(chrome.version, firefox.version);
  for (const m of [chrome, firefox]) {
    assert.deepEqual(m.permissions, ["nativeMessaging", "contextMenus", "activeTab", "scripting"]);
    assert.equal(m.host_permissions, undefined);
    assert.equal(m.content_scripts, undefined);
    assert.equal(m.externally_connectable, undefined);
    assert.equal(m.web_accessible_resources, undefined);
    assert.match(m.content_security_policy.extension_pages, /connect-src 'none'/);
  }

  for (const file of ["popup.html", "popup.js", "popup.css", "shared.js", "background.js", "icons/icon16.png", "icons/icon128.png"]) {
    assert.ok(existsSync(join(dist, "chrome", file)), file);
    assert.ok(existsSync(join(dist, "firefox", file)), file);
  }

  // Exactly the listed files ship, and no author field Chrome reads differently from Firefox.
  const shipped = (dir) => readdirSync(dir, { recursive: true }).map((f) => String(f).split("\\").join("/")).filter((f) => f.includes(".")).sort();
  assert.deepEqual(shipped(join(dist, "chrome")), ["background.js", "icons/icon128.png", "icons/icon16.png", "icons/icon32.png", "icons/icon48.png",
    "manifest.json", "popup.css", "popup.html", "popup.js", "shared.js"]);
  assert.deepEqual(shipped(join(dist, "firefox")), shipped(join(dist, "chrome")));
  assert.equal(chrome.author, undefined);

  // Nothing in the extension stores data or reaches the network by itself.
  for (const file of readdirSync(join(dist, "chrome")).filter((f) => f.endsWith(".js") || f.endsWith(".html"))) {
    const text = readFileSync(join(dist, "chrome", file), "utf8");
    for (const banned of ["localStorage", "indexedDB", "storage.local", "storage.sync", "storage.session", "fetch(", "XMLHttpRequest", "WebSocket", "eval(", "innerHTML"]) {
      assert.ok(!text.includes(banned), `${file} uses ${banned}`);
    }
  }
});

test("zips are reproducible and readable", () => {
  build();
  const dir = mkdtempSync(join(tmpdir(), "xw-ext-"));
  try {
    zip(join(here, "..", "dist", "firefox"), join(dir, "a.zip"));
    zip(join(here, "..", "dist", "firefox"), join(dir, "b.zip"));
    const a = readFileSync(join(dir, "a.zip"));
    assert.deepEqual(a, readFileSync(join(dir, "b.zip")));
    assert.equal(a.readUInt32LE(0), 0x04034b50);
    assert.equal(a.readUInt32LE(a.length - 22), 0x06054b50);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});
