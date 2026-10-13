// XaultWallet companion: what the popup and the background share. A classic script (no modules), so
// it loads the same way in Chrome's service worker, Firefox's background page, the popup and Node's
// tests. It keeps nothing: no storage, no keys, no balances. Every message to XaultWallet goes through
// the browser's native messaging, and every reply is checked before anything is shown.
(function (root) {
  "use strict";

  const HOST = "dev.dboudreau.xaultwallet";
  const PROTOCOL_VERSION = 1;
  const BASE58 = "1-9A-HJ-NP-Za-km-z";

  // A standard or sub-address (95 characters) or an integrated one (106), on any network: 4 and 8
  // mainnet, 5 and 7 stagenet, 9, A and B testnet. Only the shape: XaultWallet checks the rest.
  const ADDRESS = new RegExp(`^[45789AB][${BASE58}]{94}(?:[${BASE58}]{11})?$`);
  const ADDRESS_IN_TEXT = new RegExp(`(?<![${BASE58}])[45789AB][${BASE58}]{94}(?:[${BASE58}]{11})?(?![${BASE58}])`, "g");
  const MAX_URI = 4096;

  // A selection longer than this isn't "an address": it is most of a page, with whatever it hides.
  const MAX_SELECTION = 300;

  // What XaultWallet refuses in a link and drops from text it shows (the same rule as the app): control
  // and format characters (line breaks, the overrides that make text read backwards, invisible joiners),
  // line and paragraph separators, private-use, unassigned and broken (lone surrogate) characters.
  const HIDDEN = /[\p{Cc}\p{Cf}\p{Co}\p{Cn}\p{Cs}\p{Zl}\p{Zp}]/u;
  const HIDDEN_ALL = /[\p{Cc}\p{Cf}\p{Co}\p{Cn}\p{Cs}\p{Zl}\p{Zp}]/gu;

  // What a reply may say, and nothing else.
  const ERROR_CODES = new Set(["bad-request", "too-large", "unsupported-version", "unknown-type", "not-running", "no-vault",
    "locked", "not-ready", "busy", "watch-only", "too-many", "failed"]);
  const STATES = new Set(["unlocked", "locked", "setup"]);
  const RESULTS = new Set(["shown", "after-unlock", "waiting", "held"]);
  // Each network, with the first characters of its addresses (a private test chain uses mainnet's).
  const NETWORKS = new Map([["Mainnet", "48"], ["Regtest", "48"], ["Stagenet", "57"], ["Testnet", "9AB"]]);

  // How long a request may take: a payment or "show" may have to start XaultWallet first.
  const TIMEOUTS = new Map([["pay", 45000], ["show", 45000]]);
  const DEFAULT_TIMEOUT = 15000;

  function api() {
    return root.browser ?? root.chrome;
  }

  function newId() {
    const bytes = new Uint8Array(8);
    root.crypto.getRandomValues(bytes);
    return Array.from(bytes, (b) => b.toString(16).padStart(2, "0")).join("");
  }

  function isAddress(text) {
    return typeof text === "string" && ADDRESS.test(text);
  }

  function isMoneroUri(text) {
    return typeof text === "string" && /^monero:/i.test(text.trim());
  }

  // At most `max` UTF-16 units, never splitting a character.
  function fit(text, max) {
    let out = "";
    for (const ch of String(text ?? "")) {
      if (out.length + ch.length > max) {
        break;
      }
      out += ch;
    }
    return out;
  }

  // Text from a link, a page or a reply, as it may be shown: every run of whitespace one space, hidden
  // characters gone, at most `max` UTF-16 units (cut between characters, ending in "...").
  function cleanText(text, max) {
    const flat = String(text ?? "").replace(/\s+/gu, " ").replace(HIDDEN_ALL, "").replace(/ {2,}/g, " ").trim();
    return flat.length <= max ? flat : fit(flat, max - 3).trimEnd() + "...";
  }

  // The parts of a monero: link worth showing. Null when it isn't one (or isn't one link).
  function parseUri(text) {
    if (!isMoneroUri(text)) {
      return null;
    }

    const t = text.trim();
    if (t.length > MAX_URI || HIDDEN.test(t)) {
      return null;
    }

    let rest = t.slice("monero:".length);
    if (rest.startsWith("//")) {
      rest = rest.slice(2);
    }

    const q = rest.indexOf("?");
    const address = (q < 0 ? rest : rest.slice(0, q)).trim();
    if (!isAddress(address)) {
      return null;
    }

    const out = { address, amount: "", description: "", recipient: "" };
    if (q >= 0) {
      for (const pair of rest.slice(q + 1).split("&")) {
        const eq = pair.indexOf("=");
        const name = (eq < 0 ? pair : pair.slice(0, eq)).trim().toLowerCase();
        let value = "";
        try {
          value = eq < 0 ? "" : decodeURIComponent(pair.slice(eq + 1).replace(/\+/g, " ")).trim();
        } catch {
          return null;
        }

        if (name === "tx_amount") {
          if (!/^[0-9]{1,8}(\.[0-9]{1,12})?$/.test(value)) {
            return null;
          }
          out.amount = value;
        } else if (name === "tx_description") {
          out.description = cleanText(value, 200);
        } else if (name === "recipient_name") {
          out.recipient = cleanText(value, 100);
        }
      }
    }

    return out;
  }

  // What the user pasted or selected, as a link XaultWallet can open: a monero: link as it is, a bare
  // address as monero:<address>, or a short selection holding exactly one address. Null for anything else.
  function toPayable(text) {
    if (typeof text !== "string") {
      return null;
    }

    const t = text.trim();
    if (isMoneroUri(t)) {
      return parseUri(t) ? t : null;
    }

    if (isAddress(t)) {
      return "monero:" + t;
    }

    if (t.length > MAX_SELECTION || HIDDEN.test(t.replace(/\s+/gu, " "))) {
      return null;
    }

    ADDRESS_IN_TEXT.lastIndex = 0;
    const found = t.match(ADDRESS_IN_TEXT);
    return found && found.length === 1 ? "monero:" + found[0] : null;
  }

  // The page's own origin (scheme://host[:port]) when it is a web page; "" otherwise.
  function originOf(url) {
    try {
      const u = new URL(url);
      return u.protocol === "https:" || u.protocol === "http:" ? u.origin : "";
    } catch {
      return "";
    }
  }

  function hostOf(url) {
    try {
      const u = new URL(url);
      return u.protocol === "https:" || u.protocol === "http:" ? u.hostname : "";
    } catch {
      return "";
    }
  }

  // A plain http page that isn't on this computer: anyone on the network path could have changed it.
  function isInsecurePage(url) {
    try {
      const u = new URL(url);
      return u.protocol === "http:" && !["localhost", "127.0.0.1", "[::1]"].includes(u.hostname) && !u.hostname.endsWith(".localhost");
    } catch {
      return false;
    }
  }

  function badReply(message) {
    return { ok: false, error: { code: "bad-reply", message } };
  }

  // A reply is used only if it is XaultWallet's answer to this very request, in this version, and only
  // the fields that kind of request answers with, each checked, are passed on.
  function checkReply(reply, id, type) {
    if (!reply || typeof reply !== "object" || Array.isArray(reply) || reply.v !== PROTOCOL_VERSION || reply.id !== id || typeof reply.ok !== "boolean") {
      return badReply("XaultWallet sent a reply the companion can't use. Update the app and the extension.");
    }

    if (!reply.ok) {
      const e = reply.error && typeof reply.error === "object" ? reply.error : {};
      return {
        ok: false,
        error: {
          code: typeof e.code === "string" && ERROR_CODES.has(e.code) ? e.code : "failed",
          message: (typeof e.message === "string" && cleanText(e.message, 300)) || "XaultWallet couldn't do that.",
        },
      };
    }

    if (type === "status") {
      const network = typeof reply.network === "string" && NETWORKS.has(reply.network) ? reply.network : "";
      if (!STATES.has(reply.state) || (reply.network !== undefined && !network)) {
        return badReply("XaultWallet sent a status the companion can't use. Update the app and the extension.");
      }

      return {
        ok: true,
        state: reply.state,
        network,
        watchOnly: reply.watchOnly === true,
        ready: reply.ready === true,
        version: typeof reply.version === "string" && /^[0-9A-Za-z.+-]{1,32}$/.test(reply.version) ? reply.version : "",
      };
    }

    if (type === "pay") {
      return RESULTS.has(reply.result) ? { ok: true, result: reply.result } : badReply("XaultWallet's reply didn't say where the payment went.");
    }

    if (type === "receive") {
      const prefixes = typeof reply.network === "string" ? NETWORKS.get(reply.network) : undefined;
      return isAddress(reply.address) && reply.address.length === 95 && prefixes !== undefined && prefixes.includes(reply.address[0])
        ? { ok: true, address: reply.address, network: reply.network }
        : badReply("XaultWallet's reply had no valid address.");
    }

    return { ok: true };
  }

  // What the browser says when the native host can't be reached, in words the user can act on.
  function hostError(error) {
    const text = String((error && error.message) || error || "");
    if (/not found|no such native application/i.test(text)) {
      return { code: "not-connected", message: "This browser isn't connected to XaultWallet yet. In XaultWallet: Settings, Browser companion, Connect." };
    }

    if (/forbidden|does not have permission/i.test(text)) {
      return { code: "not-allowed", message: "XaultWallet doesn't accept this copy of the extension. Install the companion from XaultWallet's release." };
    }

    return { code: "host-error", message: "XaultWallet's connection to the browser failed. Try again; if it keeps failing, reconnect the browser in XaultWallet's Settings." };
  }

  // One request to XaultWallet. Always resolves, in time: { ok: true, ... } or { ok: false, error: { code, message } }.
  async function ask(request, timeoutMs = TIMEOUTS.get(request.type) ?? DEFAULT_TIMEOUT) {
    const message = Object.assign({ v: PROTOCOL_VERSION, id: newId() }, request);
    let timer;
    try {
      const timeout = new Promise((_, reject) => {
        timer = setTimeout(() => reject(new Error("xaultwallet-timeout")), timeoutMs);
      });
      const reply = await Promise.race([api().runtime.sendNativeMessage(HOST, message), timeout]);
      return checkReply(reply, message.id, request.type);
    } catch (error) {
      return {
        ok: false,
        error: error && error.message === "xaultwallet-timeout"
          ? { code: "timeout", message: "XaultWallet didn't answer in time. Try again in a moment." }
          : hostError(error),
      };
    } finally {
      clearTimeout(timer);
    }
  }

  // Runs inside the page (scripting.executeScript, only when the popup is opened on it): monero: links
  // and Monero addresses a person can see on the page. Hidden text, transparent or off-screen elements,
  // hidden and password fields are left out. Returns them; it changes nothing and sends nothing.
  function scanPage() {
    const base58 = "1-9A-HJ-NP-Za-km-z";
    const inText = new RegExp(`(?<![${base58}])[45789AB][${base58}]{94}(?:[${base58}]{11})?(?![${base58}])`, "g");
    const links = [];
    const addresses = [];
    const seen = new Set();
    const onPage = (r) => r.width >= 8 && r.height >= 6 && r.right + (globalThis.scrollX || 0) > 0 && r.bottom + (globalThis.scrollY || 0) > 0;
    const shown = (el) => {
      if (!el || typeof el.getBoundingClientRect !== "function") {
        return false;
      }

      if (typeof el.checkVisibility === "function"
          && !el.checkVisibility({ checkOpacity: true, checkVisibilityCSS: true, opacityProperty: true, visibilityProperty: true })) {
        return false;
      }

      return onPage(el.getBoundingClientRect());
    };
    const add = (address) => {
      if (!seen.has(address) && addresses.length < 10) {
        seen.add(address);
        addresses.push(address);
      }
    };

    for (const a of document.querySelectorAll("a[href]")) {
      const href = (a.getAttribute("href") || "").trim();
      if (/^monero:/i.test(href) && href.length <= 4096 && !seen.has(href) && shown(a)) {
        seen.add(href);
        links.push(href);
        if (links.length >= 10) {
          break;
        }
      }
    }

    // Text, node by node: the text itself must take room on the page, inside an element that shows.
    const walker = document.createTreeWalker(document.body || document.documentElement, 4 /* NodeFilter.SHOW_TEXT */);
    let budget = 500000;
    for (let node = walker.nextNode(); node && budget > 0 && addresses.length < 10; node = walker.nextNode()) {
      const text = node.nodeValue || "";
      if (text.length < 95) {
        continue;
      }

      budget -= text.length;
      const matches = [...text.slice(0, 20000).matchAll(inText)];
      if (matches.length === 0 || !shown(node.parentElement)) {
        continue;
      }

      const range = document.createRange();
      range.selectNodeContents(node);
      if (onPage(range.getBoundingClientRect())) {
        matches.forEach((m) => add(m[0]));
      }
    }

    for (const field of document.querySelectorAll("input, textarea")) {
      const kind = (field.getAttribute("type") || "text").toLowerCase();
      if (kind === "hidden" || kind === "password" || typeof field.value !== "string" || field.value.length > 4096 || !shown(field)) {
        continue;
      }

      for (const match of field.value.matchAll(inText)) {
        add(match[0]);
      }
    }

    return { links, addresses };
  }

  const shared = { HOST, PROTOCOL_VERSION, newId, isAddress, isMoneroUri, fit, cleanText, parseUri, toPayable, originOf, hostOf, isInsecurePage, checkReply, hostError, ask, scanPage };
  root.XaultShared = shared;
  if (typeof module !== "undefined" && module.exports) {
    module.exports = shared;
  }
})(typeof globalThis !== "undefined" ? globalThis : this);
