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

const WIDGET_ID = "charcoal-digital-clock";
const SCALE = 1;
const USE_12_HOUR_CLOCK = false;
const SHOW_SECONDS = true;
const UPDATE_INTERVAL_MS = 250;
const NYX_SYNC_INTERVAL_MS = 60_000;

document.documentElement.style.setProperty("--widget-scale", String(SCALE));

const hoursNode = document.querySelector("[data-hours]");
const minutesNode = document.querySelector("[data-minutes]");
const secondsNode = document.querySelector("[data-seconds]");
const periodNode = document.querySelector("[data-period]");
const statusNode = document.querySelector("[data-status]");
let epochOffset = 0;

const render = () => {
  const now = new Date(Date.now() + epochOffset);
  let hours = now.getHours();
  const period = hours >= 12 ? "PM" : "AM";
  if (USE_12_HOUR_CLOCK) hours = hours % 12 || 12;
  hoursNode.textContent = String(hours).padStart(2, "0");
  minutesNode.textContent = String(now.getMinutes()).padStart(2, "0");
  secondsNode.textContent = SHOW_SECONDS ? String(now.getSeconds()).padStart(2, "0") : "";
  secondsNode.parentElement.querySelectorAll(".clock__separator")[1].hidden = !SHOW_SECONDS;
  periodNode.hidden = !USE_12_HOUR_CLOCK;
  periodNode.textContent = period;
};

const syncTime = async () => {
  if (!hasAetherBridge()) return;
  try {
    const epoch = await readNyxTime();
    if (Number.isFinite(epoch)) {
      epochOffset = epoch - Date.now();
      statusNode.hidden = true;
    }
  } catch {
    statusNode.hidden = false;
    statusNode.textContent = "bridge unavailable";
  }
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
