import { destroyWidget, onPageCleanup } from "@khaosshell/widget-runtime/lifecycle.js";
import { getAetherVariable, hasAetherBridge, readNyxTime } from "@khaosshell/widget-runtime/bridge.js";

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

const WIDGET_ID = "rainmeter-mond-clock";
const SCALE = 1;
const DEFAULT_SCALE = SCALE;
const BASE_ZOOM = 1.2;
const DEFAULT_PRIMARY = "#000000";
const PRIMARY_VARIABLE = "widget.rainmeter-mond-clock.primary";
const SCALE_VARIABLE = "widget.rainmeter-mond-clock.scale";

const renderer = window?.renderer;
const widgetNode = document.querySelector(".mond");
const isCssColor = (value) => {
  if (typeof value !== "string" || !value.trim()) return false;
  if (typeof window?.CSS?.supports !== "function") return true;
  try {
    return window.CSS.supports("color", value.trim());
  } catch {
    return false;
  }
};
const configuredPrimary = getAetherVariable(PRIMARY_VARIABLE, DEFAULT_PRIMARY, renderer);
const primary = isCssColor(configuredPrimary) ? configuredPrimary.trim() : DEFAULT_PRIMARY;
const configuredScale = Number(getAetherVariable(SCALE_VARIABLE, DEFAULT_SCALE, renderer));
const scale = Number.isFinite(configuredScale) && configuredScale > 0 ? configuredScale : DEFAULT_SCALE;

document.documentElement.style.setProperty("--widget-scale", String(scale));
widgetNode.style.setProperty("--mond-color", primary);
widgetNode.style.setProperty("--mond-zoom", String(BASE_ZOOM * scale));


const weekdayNode = document.querySelector("[data-weekday]");
const dateNode = document.querySelector("[data-date]");
const timeNode = document.querySelector("[data-time]");
const render = () => {
  const now = new Date(Date.now() + epochOffset);
  weekdayNode.textContent = new Intl.DateTimeFormat("en-US", { weekday: "long" }).format(now).toUpperCase();
  dateNode.textContent = String(now.getDate()).padStart(2, "0") + " " + new Intl.DateTimeFormat("en-US", { month: "long" }).format(now) + ", " + now.getFullYear() + ".";
  const hour = now.getHours() % 12 || 12;
  const period = now.getHours() >= 12 ? "PM" : "AM";
  timeNode.textContent = String(hour) + ":" + String(now.getMinutes()).padStart(2, "0") + " " + period;
};

let epochOffset = 0;

const syncTime = async () => {
  if (!hasAetherBridge(renderer)) return;
  try {
    const epoch = await readNyxTime(renderer);
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
