import { destroyWidget, onPageCleanup } from "@khaosshell/widget-runtime/lifecycle.js";
import { hasAetherBridge, readNyxTime } from "@khaosshell/widget-runtime/bridge.js";

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

const WIDGET_ID = "current-date";
const SCALE = 1;

document.documentElement.style.setProperty("--widget-scale", String(SCALE));


const LOCALE = "en-US";
const dateKey = (date) => [date.getFullYear(), String(date.getMonth() + 1).padStart(2, "0"), String(date.getDate()).padStart(2, "0")].join("-");
const cells = [...document.querySelectorAll('[data-webskin-calendar-part="date-cell"]')];
const render = () => {
  const today = new Date(Date.now() + epochOffset);
  
  document.querySelector('[data-webskin-calendar-part="weekday"]').textContent = new Intl.DateTimeFormat(LOCALE, { weekday: "long" }).format(today);
  document.querySelector('[data-webskin-calendar-part="day"]').textContent = String(today.getDate());
  document.querySelector('[data-webskin-calendar-part="month"]').textContent = new Intl.DateTimeFormat(LOCALE, { month: "long" }).format(today);
  document.querySelector('[data-webskin-calendar-part="year"]').textContent = String(today.getFullYear());
  
  
};

let epochOffset = 0;

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
