// XaultWallet companion popup. Asks XaultWallet for its state, offers the payment links found on this
// page (looked for only now, while the popup is open), passes a pasted address or link to XaultWallet's
// Send tab, and gets a new receive address. It shows; XaultWallet decides; nothing is kept.
"use strict";

const { ask, fit, parseUri, toPayable, originOf, hostOf, isInsecurePage, scanPage } = globalThis.XaultShared;
const browserApi = globalThis.browser ?? globalThis.chrome;
const $ = (id) => document.getElementById(id);

let tabUrl = "";
let state = "unknown";

// Where a payment request went, by the app's answer.
const WHERE = new Map([
  ["shown", "It's in XaultWallet's Send tab: check it there and confirm."],
  ["after-unlock", "It opens in XaultWallet's Send tab once you unlock."],
  ["waiting", "It opens in XaultWallet's Send tab when you close Settings there."],
  ["held", "XaultWallet's Send form is in use: open the request from the notice in XaultWallet."],
]);

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
  $("version").textContent = reply.version ? "XaultWallet " + reply.version : "";
  if (reply.state === "unlocked" && !reply.ready) {
    state = "starting";
    setStatus("Starting…", "warn", "The wallet on screen is still starting. Payments can be opened now; ask for an address in a moment.");
  } else if (reply.state === "unlocked") {
    const watch = reply.watchOnly ? "The wallet on screen is watch-only: it can receive, not pay." : "";
    setStatus("Unlocked" + (reply.network ? " · " + reply.network : ""), "ok", watch);
  } else if (reply.state === "locked") {
    setStatus("Locked", "warn", "Payments open after you unlock XaultWallet. Unlock it to get an address.");
  } else {
    setStatus("Not set up", "warn", "XaultWallet has no wallet yet. Set it up in the app first.");
  }

  updateButtons();
}

function updateButtons() {
  const reachable = ["unlocked", "starting", "locked", "not-running", "setup"].includes(state);
  $("pay-button").disabled = !reachable || state === "setup";
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

  say(WHERE.get(reply.result) ?? WHERE.get("shown"), true);
}

async function receive() {
  say("");
  $("receive-button").disabled = true;
  try {
    const reply = await ask({ type: "receive", label: fit($("label-input").value.trim(), 40), origin: originOf(tabUrl) });
    if (!reply.ok) {
      say(reply.error.message);
      return;
    }

    $("address").textContent = reply.address;
    $("address-box").hidden = false;
    say(reply.network !== "Mainnet" ? `A ${reply.network} address: it can't receive real XMR.` : "", false);
  } finally {
    $("receive-button").disabled = state !== "unlocked";
  }
}

async function copyAddress() {
  const text = $("address").textContent;
  if (!text) {
    return;
  }

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

// One found payment: what the app can vouch for (amount, address) plainly, and what the page wrote about
// it in quotes, isolated so it can't turn the text around it.
function foundItem(item) {
  const li = document.createElement("li");
  const what = document.createElement("div");
  what.className = "what";

  const who = document.createElement("div");
  who.className = "who";
  who.textContent = item.parsed.amount ? `${item.parsed.amount} XMR` : "Monero address";
  what.append(who);

  const words = [item.parsed.recipient, item.parsed.description].filter((w) => w).join(": ");
  if (words) {
    const says = document.createElement("div");
    says.className = "says";
    const quoted = document.createElement("bdi");
    quoted.textContent = words;
    says.append("“", quoted, "”");
    what.append(says);
  }

  const addr = document.createElement("div");
  addr.className = "addr";
  addr.textContent = shortAddress(item.parsed.address);
  what.append(addr);

  const button = document.createElement("button");
  button.type = "button";
  button.textContent = "Pay";
  button.title = "Open this payment in XaultWallet";
  button.addEventListener("click", () => pay(item.uri));
  li.append(what, button);
  return li;
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
  for (const link of found.links.slice(0, 10)) {
    const parsed = typeof link === "string" ? parseUri(link) : null;
    if (parsed && !seen.has(parsed.address + parsed.amount)) {
      seen.add(parsed.address + parsed.amount);
      items.push({ uri: link.trim(), parsed });
    }
  }

  for (const address of found.addresses.slice(0, 10)) {
    if (typeof address === "string" && !items.some((i) => i.parsed.address === address)) {
      const parsed = parseUri("monero:" + address);
      if (parsed) {
        items.push({ uri: "monero:" + address, parsed });
      }
    }
  }

  $("found").replaceChildren(...items.slice(0, 6).map(foundItem));
  $("page-source").textContent = `Written by ${hostOf(tabUrl)}. Check them against what you expect before you pay.`;
  $("page-insecure").hidden = !isInsecurePage(tabUrl);
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
  $("label-input").value = fit(hostOf(tabUrl), 40);
  try {
    // A problem flagged on the toolbar button has been seen.
    await browserApi.action.setBadgeText({ text: "" });
    await browserApi.action.setTitle({ title: "XaultWallet" });
  } catch {
    // not available everywhere
  }

  await Promise.all([refreshStatus(), findOnPage(tab && tab.id)]);
}

start();
