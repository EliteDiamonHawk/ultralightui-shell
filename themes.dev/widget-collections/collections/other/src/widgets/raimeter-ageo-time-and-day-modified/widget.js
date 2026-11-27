import { destroyWidget, onPageCleanup } from "@khaosshell/widget-runtime/lifecycle.js";
import { getAetherVariable, hasAetherBridge, readNyxTime } from "@khaosshell/widget-runtime/bridge.js";
import quoteSource from "./assets/ListOfQuotes.json";

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

  const WIDGET_ID = "raimeter-ageo-time-and-day-modified";
  const SCALE = 1.05;
  const DEFAULT_SCALE = SCALE;
  const DEFAULT_PRIMARY = "rgb(0 0 0)";
  const DEFAULT_ACCENT = "rgb(255 187 64)";
  const PRIMARY_VARIABLE = "widget.ageo-day-and-time-modified.primary";
  const ACCENT_VARIABLE = "widget.ageo-day-and-time-modified.accent";
  const SCALE_VARIABLE = "widget.ageo-day-and-time-modified.scale";
  const UPDATE_INTERVAL_MS = 1_000;
  const NYX_SYNC_INTERVAL_MS = 60_000;
  const LOCALE = "en-US";

  const renderer = window?.renderer;
  const widgetNode = document.querySelector(".ageo");
  const isCssColor = (value) => {
    if (typeof value !== "string" || !value.trim()) return false;
    if (typeof window?.CSS?.supports !== "function") return true;
    try {
      return window.CSS.supports("color", value.trim());
    } catch {
      return false;
    }
  };
  const readCssColor = (key, fallback) => {
    const value = getAetherVariable(key, fallback, renderer);
    return isCssColor(value) ? value.trim() : fallback;
  };
  const configuredScale = Number(getAetherVariable(SCALE_VARIABLE, DEFAULT_SCALE, renderer));
  const scale = Number.isFinite(configuredScale) && configuredScale > 0 ? configuredScale : DEFAULT_SCALE;

  widgetNode.style.setProperty("--ageo-primary", readCssColor(PRIMARY_VARIABLE, DEFAULT_PRIMARY));
  widgetNode.style.setProperty("--ageo-accent", readCssColor(ACCENT_VARIABLE, DEFAULT_ACCENT));
  document.documentElement.style.setProperty("--widget-scale", String(scale));

  const dateNode = document.querySelector(".ageo__date");
  const dayNode = document.querySelector("[data-day]");
  const monthNode = document.querySelector("[data-month]");
  const weekdayNode = document.querySelector("[data-weekday]");
  const timeNode = document.querySelector("[data-time]");
  const periodNode = document.querySelector("[data-period]");
  const timeDateNode = document.querySelector("[data-time-datetime]");
  const quoteNode = document.querySelector("[data-quote]");
  let epochOffset = 0;

  const quotes = Array.isArray(quoteSource)
    ? quoteSource.filter((quote) => typeof quote === "string" && quote.trim()).map((quote) => quote.trim())
    : [];

  const renderQuote = () => {
    if (quotes.length > 0) quoteNode.textContent = quotes[Math.floor(Math.random() * quotes.length)];
  };

  const render = () => {
    const now = new Date(Date.now() + epochOffset);
    const hour = now.getHours();
    const minute = String(now.getMinutes()).padStart(2, "0");
    const hour12 = hour % 12 || 12;

    dayNode.textContent = String(now.getDate()).padStart(2, "0");
    monthNode.textContent = new Intl.DateTimeFormat(LOCALE, { month: "long" }).format(now);
    weekdayNode.textContent = new Intl.DateTimeFormat(LOCALE, { weekday: "long" }).format(now).toUpperCase();
    timeNode.textContent = `${hour12}:${minute}`;
    periodNode.textContent = hour >= 12 ? "PM" : "AM";
    dateNode.dateTime = now.toISOString().slice(0, 10);
    timeDateNode.dateTime = `${String(hour12).padStart(2, "0")}:${minute}`;
  };

  const syncTime = async () => {
    if (!hasAetherBridge(renderer)) return;
    try {
      const epoch = await readNyxTime(renderer);
      if (Number.isFinite(epoch)) epochOffset = epoch - Date.now();
    } catch {}
  };

  renderQuote();
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
