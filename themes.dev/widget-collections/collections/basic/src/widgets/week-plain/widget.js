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

const WIDGET_ID = "week-plain";
const SCALE = 1;
document.documentElement.style.setProperty("--widget-scale", String(SCALE));

const cells = [...document.querySelectorAll('[data-basic-part="week-cell"]')];
let epochOffset = 0;
const dateKey = (date) => [date.getFullYear(), date.getMonth(), date.getDate()].join("-");

const render = () => {
  const now = new Date(Date.now() + epochOffset);
  const start = new Date(now);
  start.setDate(now.getDate() - now.getDay());
  cells.forEach((cell, index) => {
    const date = new Date(start);
    date.setDate(start.getDate() + index);
    cell.querySelector('[data-basic-part="weekday"]').textContent = new Intl.DateTimeFormat("en-US", { weekday: "short" }).format(date);
    cell.querySelector('[data-basic-part="day"]').textContent = String(date.getDate());
    cell.querySelector('[data-basic-part="month"]').textContent = new Intl.DateTimeFormat("en-US", { month: "short" }).format(date);
    cell.dataset.basicToday = String(dateKey(date) === dateKey(now));
  });
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
const renderTimer = window.setInterval(render, 60_000);
const syncTimer = window.setInterval(syncTime, 60_000);
onPageCleanup(root, () => {
  window.clearInterval(renderTimer);
  window.clearInterval(syncTimer);
});

void WIDGET_ID;

  return { destroy: () => destroyWidget(root) };
}

if (globalThis.__KHAOSSHELL_WIDGET_AUTOSTART__) mount(globalThis.document);
