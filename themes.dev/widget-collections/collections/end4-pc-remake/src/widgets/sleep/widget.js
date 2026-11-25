import { destroyWidget, onPageCleanup } from "@khaosshell/widget-runtime/lifecycle.js";
import { callNyxControl, hasAetherBridge } from "@khaosshell/widget-runtime/bridge.js";

export function mount(root = globalThis.document, options = {}) {
  if (!root) throw new Error("KhaosShell widget mount requires a root element or document.");
  const pageDocument = root.ownerDocument ?? root;
  const queryRoot = root.querySelector ? root : pageDocument;
  const document = new Proxy(pageDocument, {
    get(target, property, receiver) {
      if (property === "querySelector") return queryRoot.querySelector.bind(queryRoot);
      if (property === "querySelectorAll") return queryRoot.querySelectorAll.bind(queryRoot);
      const value = Reflect.get(target, property, receiver);
      return typeof value === "function" ? value.bind(target) : value;
    },
  });
  const window = options.window ?? pageDocument.defaultView ?? globalThis.window;

const WIDGET_ID = "sleep";
const SCALE = 1;

document.documentElement.style.setProperty("--widget-scale", String(SCALE));


const button = document.querySelector("[data-control]");
const statusNode = document.querySelector("[data-status]");
const CONTROL = "sleep";
const SAFETY_CLICKS = 1;
let clicks = 0;
let resetTimer;

button.addEventListener("click", async () => {
  clicks += 1;
  window.clearTimeout(resetTimer);
  resetTimer = window.setTimeout(() => { clicks = 0; }, 3000);
  if (clicks < SAFETY_CLICKS) {
    statusNode.textContent = "click " + (SAFETY_CLICKS - clicks) + " more time" + (SAFETY_CLICKS - clicks === 1 ? "" : "s") + " to confirm";
    return;
  }
  clicks = 0;
  if (!hasAetherBridge()) {
    statusNode.textContent = "preview only";
    return;
  }
  button.disabled = true;
  statusNode.textContent = CONTROL + " requested";
  try {
    await callNyxControl(CONTROL);
    statusNode.textContent = CONTROL + " sent to Nyx";
  } catch (error) {
    statusNode.textContent = error.message;
  } finally {
    button.disabled = false;
  }
});
onPageCleanup(root, () => window.clearTimeout(resetTimer));

void WIDGET_ID;

  return { destroy: () => destroyWidget(root) };
}

if (globalThis.__KHAOSSHELL_WIDGET_AUTOSTART__) mount(globalThis.document);
