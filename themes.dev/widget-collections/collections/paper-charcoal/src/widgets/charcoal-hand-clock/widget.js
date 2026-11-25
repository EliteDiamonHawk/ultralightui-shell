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

const WIDGET_ID = "charcoal-hand-clock";
const SCALE = 1;
const UPDATE_INTERVAL_MS = 250;
const NYX_SYNC_INTERVAL_MS = 60_000;

document.documentElement.style.setProperty("--widget-scale", String(SCALE));

const hourHand = document.querySelector("[data-hour-hand]");
const minuteHand = document.querySelector("[data-minute-hand]");
const secondHand = document.querySelector("[data-second-hand]");
const label = document.querySelector("[data-label]");
let epochOffset = 0;

const render = () => {
  const now = new Date(Date.now() + epochOffset);
  const seconds = now.getSeconds() + now.getMilliseconds() / 1000;
  const minutes = now.getMinutes() + seconds / 60;
  const hours = (now.getHours() % 12) + minutes / 60;
  hourHand.style.transform = "rotate(" + (hours * 30) + "deg)";
  minuteHand.style.transform = "rotate(" + (minutes * 6) + "deg)";
  secondHand.style.transform = "rotate(" + (seconds * 6) + "deg)";
  label.textContent = [now.getHours(), now.getMinutes(), now.getSeconds()]
    .map((value) => String(value).padStart(2, "0"))
    .join(":");
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
const renderTimer = window.setInterval(render, UPDATE_INTERVAL_MS);
const syncTimer = window.setInterval(syncTime, NYX_SYNC_INTERVAL_MS);

onPageCleanup(root, () => {
  window.clearInterval(renderTimer);
  window.clearInterval(syncTimer);
});

void WIDGET_ID;

  return { destroy: () => destroyWidget(root) };
}

if (globalThis.__KHAOSSHELL_WIDGET_AUTOSTART__) mount(globalThis.document);
