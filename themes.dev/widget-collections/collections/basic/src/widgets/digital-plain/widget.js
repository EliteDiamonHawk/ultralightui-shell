import { hasAetherBridge, readNyxTime } from "@khaosshell/widget-runtime/bridge.js";
import { destroyWidget, onPageCleanup } from "@khaosshell/widget-runtime/lifecycle.js";

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

const WIDGET_ID = "digital-plain";
const SCALE = 1;
document.documentElement.style.setProperty("--widget-scale", String(SCALE));

const hoursNode = document.querySelector('[data-basic-part="hours"]');
const minutesNode = document.querySelector('[data-basic-part="minutes"]');
const secondsNode = document.querySelector('[data-basic-part="seconds"]');
const periodNode = document.querySelector('[data-basic-part="period"]');
let epochOffset = 0;

const render = () => {
  const now = new Date(Date.now() + epochOffset);
  const hours = now.getHours();
  hoursNode.textContent = String(hours % 12 || 12).padStart(2, "0");
  minutesNode.textContent = String(now.getMinutes()).padStart(2, "0");
  secondsNode.textContent = String(now.getSeconds()).padStart(2, "0");
  periodNode.textContent = hours >= 12 ? "PM" : "AM";
};

const syncTime = async () => {
  if (!hasAetherBridge()) return;
  try {
    const epoch = await readNyxTime();
    if (Number.isFinite(epoch)) epochOffset = epoch - Date.now();
  } catch {}
};

render();
void syncTime();
const renderTimer = window.setInterval(render, 250);
const syncTimer = window.setInterval(syncTime, 60_000);
onPageCleanup(root, () => {
  window.clearInterval(renderTimer);
  window.clearInterval(syncTimer);
});

void WIDGET_ID;

  return { destroy: () => destroyWidget(root) };
}

if (globalThis.__KHAOSSHELL_WIDGET_AUTOSTART__) mount(globalThis.document);
