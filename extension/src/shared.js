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

  // The parts of a monero: link worth showing. Null when it isn't one (or isn't one link).
  function parseUri(text) {
    if (!isMoneroUri(text) || text.length > MAX_URI || /[\u0000-\u001f\u007f]/.test(text)) {
      return null;
    }

    let rest = text.trim().slice("monero:".length);
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
          out.description = value.slice(0, 200);
        } else if (name === "recipient_name") {
          out.recipient = value.slice(0, 100);
        }
      }
    }

    return out;
  }

  // What the user pasted or selected, as a link XaultWallet can open: a monero: link as it is, a bare
  // address as monero:<address>. Null for anything else.
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

    // A selection with an address somewhere in it.
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

  // A reply is used only if it is XaultWallet's answer to this very request, in this version.
  function checkReply(reply, id) {
    if (!reply || typeof reply !== "object" || Array.isArray(reply) || reply.v !== PROTOCOL_VERSION || reply.id !== id || typeof reply.ok !== "boolean") {
      return { ok: false, error: { code: "bad-reply", message: "XaultWallet sent a reply the companion can't use. Update the app and the extension." } };
    }

    if (!reply.ok) {
      const e = reply.error && typeof reply.error === "object" ? reply.error : {};
      return {
        ok: false,
        error: {
          code: typeof e.code === "string" ? e.code.slice(0, 40) : "failed",
          message: typeof e.message === "string" ? e.message.slice(0, 300) : "XaultWallet couldn't do that.",
        },
      };
    }

    if ("address" in reply && !isAddress(reply.address)) {
      return { ok: false, error: { code: "bad-reply", message: "XaultWallet's reply had no valid address." } };
    }

    return reply;
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

  // One request to XaultWallet. Always resolves: { ok: true, ... } or { ok: false, error: { code, message } }.
  async function ask(request) {
    const message = Object.assign({ v: PROTOCOL_VERSION, id: newId() }, request);
    try {
      const reply = await api().runtime.sendNativeMessage(HOST, message);
      return checkReply(reply, message.id);
    } catch (error) {
      return { ok: false, error: hostError(error) };
    }
  }

  // Runs inside the page (scripting.executeScript, only when the popup is opened on it): monero: links
  // and Monero addresses shown on the page. Returns them; it changes nothing and sends nothing.
  function scanPage() {
    const base58 = "1-9A-HJ-NP-Za-km-z";
    const inText = new RegExp(`(?<![${base58}])[45789AB][${base58}]{94}(?:[${base58}]{11})?(?![${base58}])`, "g");
    const links = [];
    const addresses = [];
    const seen = new Set();
    for (const a of document.querySelectorAll("a[href]")) {
      const href = (a.getAttribute("href") || "").trim();
      if (/^monero:/i.test(href) && href.length <= 4096 && !seen.has(href)) {
        seen.add(href);
        links.push(href);
        if (links.length >= 10) {
          break;
        }
      }
    }

    const texts = [(document.body && document.body.innerText) || ""];
    for (const field of document.querySelectorAll("input, textarea")) {
      if (typeof field.value === "string" && field.value.length <= 4096) {
        texts.push(field.value);
      }
    }

    for (const text of texts) {
      for (const match of text.slice(0, 500000).matchAll(inText)) {
        if (!seen.has(match[0]) && addresses.length < 10) {
          seen.add(match[0]);
          addresses.push(match[0]);
        }
      }
    }

    return { links, addresses };
  }

  const shared = { HOST, PROTOCOL_VERSION, newId, isAddress, isMoneroUri, parseUri, toPayable, originOf, hostOf, checkReply, hostError, ask, scanPage };
  root.XaultShared = shared;
  if (typeof module !== "undefined" && module.exports) {
    module.exports = shared;
  }
})(typeof globalThis !== "undefined" ? globalThis : this);
