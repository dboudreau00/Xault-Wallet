// XaultWallet companion popup. Asks XaultWallet for its state, offers the payment links found on this
// page (looked for only now, while the popup is open), passes a pasted address or link to XaultWallet's
// Send tab, and gets a new receive address. It shows; XaultWallet decides; nothing is kept.
"use strict";

const { ask, parseUri, toPayable, originOf, hostOf, scanPage } = globalThis.XaultShared;
const browserApi = globalThis.browser ?? globalThis.chrome;
const $ = (id) => document.getElementById(id);

let tabUrl = "";
let state = "unknown";

function say(text, ok = false) {
  const m = $("message");
  m.textContent = text;
  m.classList.toggle("ok", ok);
  m.hidden = !text;
}

function setStatus(text, kind, detail = "") {
  const pill = $("status");
  pill.textContent = text;
  pill.className = "pill " + kind;
  $("status-detail").textContent = detail;
  $("status-detail").hidden = !detail;
}

function shortAddress(address) {
  return address.length > 20 ? address.slice(0, 8) + "…" + address.slice(-8) : address;
}

async function refreshStatus() {
  const reply = await ask({ type: "status" });
  if (!reply.ok) {
    state = reply.error.code;
    if (reply.error.code === "not-running") {
      setStatus("Not running", "warn", "XaultWallet isn't open. Open it below, or pay a link: it starts XaultWallet.");
    } else {
      setStatus("Not connected", "off", reply.error.message);
    }

    updateButtons();
    return;
  }

  state = reply.state;
  $("version").textContent = typeof reply.version === "string" ? "XaultWallet " + reply.version.slice(0, 32) : "";
  if (reply.state === "unlocked" && reply.ready === false) {
    state = "starting";
    setStatus("Starting…", "warn", "The wallet on screen is still starting. Payments can be opened now; ask for an address in a moment.");
  } else if (reply.state === "unlocked") {
    const network = typeof reply.network === "string" ? reply.network : "";
    const watch = reply.watchOnly === true ? " (watch-only)" : "";
    setStatus("Unlocked" + (network ? " · " + network : ""), "ok", watch ? "The wallet on screen is watch-only: it can receive, not pay." : "");
  } else if (reply.state === "locked") {
    setStatus("Locked", "warn", "Payments open after you unlock XaultWallet. Unlock it to get an address.");
  } else {
    setStatus("Not set up", "warn", "XaultWallet has no wallet yet. Set it up in the app first.");
  }

  updateButtons();
}

function updateButtons() {
  const reachable = state === "unlocked" || state === "starting" || state === "locked" || state === "not-running";
  $("pay-button").disabled = !reachable;
  $("receive-button").disabled = state !== "unlocked";
  $("show-button").disabled = !reachable;
}

async function pay(uri) {
  say("");
  const payable = toPayable(uri);
  if (!payable) {
    say("That isn't a Monero address or a monero: payment link.");
    return;
  }

  const reply = await ask({ type: "pay", uri: payable, origin: originOf(tabUrl) });
  if (!reply.ok) {
    say(reply.error.message);
    return;
  }

  const where = {
    "after-unlock": "It opens in XaultWallet's Send tab once you unlock.",
    waiting: "It opens in XaultWallet's Send tab when you close Settings there.",
  }[reply.result] ?? "It's in XaultWallet's Send tab: check it there and confirm.";
  say(where, true);
}

async function receive() {
  say("");
  $("receive-button").disabled = true;
  try {
    const reply = await ask({ type: "receive", label: $("label-input").value.trim().slice(0, 40), origin: originOf(tabUrl) });
    if (!reply.ok) {
      say(reply.error.message);
      return;
    }

    $("address").textContent = reply.address;
    $("address-box").hidden = false;
    say(typeof reply.network === "string" && reply.network !== "Mainnet"
      ? `A ${reply.network} address: it can't receive real XMR.`
      : "", false);
  } finally {
    $("receive-button").disabled = state !== "unlocked";
  }
}

async function copyAddress() {
  const text = $("address").textContent;
  try {
    await navigator.clipboard.writeText(text);
    say("Address copied.", true);
  } catch {
    const range = document.createRange();
    range.selectNodeContents($("address"));
    const selection = window.getSelection();
    selection.removeAllRanges();
    selection.addRange(range);
    say("Copying was refused here: the address is selected, press Ctrl+C.");
  }
}

// Payment links and addresses on the page the popup was opened on (activeTab: only this tab, only now).
async function findOnPage(tabId) {
  if (!tabId || !originOf(tabUrl)) {
    return;
  }

  let found;
  try {
    const [result] = await browserApi.scripting.executeScript({ target: { tabId }, func: scanPage });
    found = result && result.result;
  } catch {
    return; // a page extensions may not read (the browser's own pages, the web stores)
  }

  if (!found || !Array.isArray(found.links) || !Array.isArray(found.addresses)) {
    return;
  }

  const items = [];
  const seen = new Set();
  for (const link of found.links) {
    const parsed = parseUri(link);
    if (parsed && !seen.has(parsed.address + parsed.amount)) {
      seen.add(parsed.address + parsed.amount);
      items.push({ uri: link, parsed });
    }
  }

  for (const address of found.addresses) {
    if (!items.some((i) => i.parsed.address === address)) {
      const parsed = parseUri("monero:" + address);
      if (parsed) {
        items.push({ uri: "monero:" + address, parsed });
      }
    }
  }

  const list = $("found");
  list.replaceChildren();
  for (const item of items.slice(0, 6)) {
    const li = document.createElement("li");
    const what = document.createElement("div");
    what.className = "what";
    const who = document.createElement("div");
    who.className = "who";
    const title = item.parsed.recipient || item.parsed.description || "Monero address";
    who.textContent = item.parsed.amount ? `${item.parsed.amount} XMR · ${title}` : title;
    const addr = document.createElement("div");
    addr.className = "addr";
    addr.textContent = shortAddress(item.parsed.address);
    what.append(who, addr);
    const button = document.createElement("button");
    button.type = "button";
    button.textContent = "Pay";
    button.title = "Open this payment in XaultWallet";
    button.addEventListener("click", () => pay(item.uri));
    li.append(what, button);
    list.append(li);
  }

  $("on-page").hidden = items.length === 0;
}

async function start() {
  $("pay-button").addEventListener("click", () => pay($("pay-input").value));
  $("receive-button").addEventListener("click", receive);
  $("copy-button").addEventListener("click", copyAddress);
  $("show-button").addEventListener("click", async () => {
    say("");
    const reply = await ask({ type: "show" });
    if (!reply.ok) {
      say(reply.error.message);
    } else {
      await refreshStatus();
    }
  });

  let tab;
  try {
    [tab] = await browserApi.tabs.query({ active: true, currentWindow: true });
  } catch {
    tab = undefined;
  }

  tabUrl = (tab && tab.url) || "";
  $("label-input").value = hostOf(tabUrl).slice(0, 40);
  try {
    await browserApi.action.setBadgeText({ text: "" }); // a problem flagged on the button has been seen
  } catch {
    // not available everywhere
  }

  await refreshStatus();
  await findOnPage(tab && tab.id);
}

start();
