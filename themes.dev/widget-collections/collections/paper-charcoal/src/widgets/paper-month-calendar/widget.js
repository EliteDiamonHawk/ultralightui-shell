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

const WIDGET_ID = "paper-month-calendar";
const SCALE = 1;
const UPDATE_INTERVAL_MS = 60_000;
const NYX_SYNC_INTERVAL_MS = 300_000;
const LOCALE = "en-US";

document.documentElement.style.setProperty("--widget-scale", String(SCALE));

const label = document.querySelector("[data-month-label]");
const cells = [...document.querySelectorAll("[data-day-cell]")];
let epochOffset = 0;

const dateKey = (date) => [
  date.getFullYear(),
  String(date.getMonth() + 1).padStart(2, "0"),
  String(date.getDate()).padStart(2, "0"),
].join("-");

const render = () => {
  const today = new Date(Date.now() + epochOffset);
  const monthStart = new Date(today.getFullYear(), today.getMonth(), 1);
  const gridStart = new Date(today.getFullYear(), today.getMonth(), 1 - monthStart.getDay());
  label.textContent = new Intl.DateTimeFormat(LOCALE, { month: "long", year: "numeric" }).format(today);

  cells.forEach((cell, index) => {
    const date = new Date(gridStart);
    date.setDate(gridStart.getDate() + index);
    cell.textContent = String(date.getDate());
    cell.classList.toggle("is-adjacent", date.getMonth() !== today.getMonth());
    cell.classList.toggle("is-today", dateKey(date) === dateKey(today));
    cell.setAttribute("aria-label", date.toLocaleDateString(LOCALE, { dateStyle: "full" }));
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
