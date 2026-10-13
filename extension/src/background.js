// XaultWallet companion: the right-click menu. Select a Monero address or a monero: link on a page,
// right-click, "Pay with XaultWallet": the request opens in XaultWallet's Send tab, where you review and
// confirm it. Nothing here holds or remembers anything.
"use strict";

if (typeof importScripts === "function") {
  importScripts("shared.js"); // Chrome's service worker; Firefox lists shared.js in the manifest
}

const { ask, toPayable, originOf } = globalThis.XaultShared;
const browserApi = globalThis.browser ?? globalThis.chrome;
const MENU_ID = "xaultwallet-pay-selection";

browserApi.runtime.onInstalled.addListener(() => {
  browserApi.contextMenus.create({
    id: MENU_ID,
    title: "Pay with XaultWallet",
    contexts: ["selection"],
  });
});

browserApi.contextMenus.onClicked.addListener(async (info) => {
  if (info.menuItemId !== MENU_ID) {
    return;
  }

  const uri = toPayable(info.selectionText || "");
  if (!uri) {
    await flag("Select just one Monero address or payment link (a whole page is too much to trust).");
    return;
  }

  // The frame the selection is in: a page can show another site inside it.
  const reply = await ask({ type: "pay", uri, origin: originOf(info.frameUrl || info.pageUrl || "") });
  if (reply.ok) {
    await flag("");
  } else {
    await flag(reply.error.message);
  }
});

// A problem shows on the toolbar button (a "!" and its tooltip) until the next request.
async function flag(message) {
  try {
    await browserApi.action.setBadgeText({ text: message ? "!" : "" });
    await browserApi.action.setBadgeBackgroundColor({ color: "#E0A030" });
    await browserApi.action.setTitle({ title: message ? "XaultWallet: " + message : "XaultWallet" });
  } catch {
    // the badge is a courtesy
  }
}
