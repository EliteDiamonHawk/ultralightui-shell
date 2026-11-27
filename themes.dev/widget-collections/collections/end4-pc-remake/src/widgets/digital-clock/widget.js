import { hasAetherBridge, readNyxTime } from "@khaosshell/widget-runtime/bridge.js";
import { destroyWidget, onPageCleanup } from "@khaosshell/widget-runtime/lifecycle.js";
import { applyWidgetTheme, readWidgetTheme } from "@khaosshell/widget-runtime/theme.js";
import { defaultDigitalSyntax, digitalValues, renderDigitalSyntax, resolveClockSettings, shiftedDate } from "@khaosshell/widget-runtime/clock.js";
import { widgetContext } from "@khaosshell/widget-runtime/settings.js";

const SCALE = 1;

export function mount(root = globalThis.document, options = {}) {
  if (!root) throw new Error("KhaosShell widget mount requires a root element or document.");
  const { document, window, renderer } = widgetContext(root, options);
  const WIDGET_ID = "digital-clock";
  applyWidgetTheme(document, readWidgetTheme(WIDGET_ID, { primary: "#d6d6d6", secondary: "#2e2e2e", scale: SCALE }, renderer));
  const settings = resolveClockSettings({ root, widgetId: WIDGET_ID, options, renderer, defaults: { format: "12h", seconds: true, showPeriod: true } });
  const display = document.querySelector("time");
  const hours = document.querySelector("[data-hours]");
  const minutes = document.querySelector("[data-minutes]");
  const seconds = document.querySelector("[data-seconds]");
  const period = document.querySelector("[data-period]");
  let epochOffset = 0;
  const syntax = settings.syntax ?? defaultDigitalSyntax(settings);
  const render = () => {
    const now = shiftedDate(new Date(Date.now() + epochOffset), settings.offsetMinutes);
    const values = digitalValues(now, settings.format);
    if (settings.syntax) {
      display.textContent = renderDigitalSyntax(syntax, values);
    } else {
      hours.textContent = settings.format === "24h" ? values.HH : values.hh;
      minutes.textContent = values.mm;
      seconds.textContent = settings.seconds ? values.ss : "";
      seconds.parentElement.querySelectorAll(".digital-prestyled__seconds-separator").forEach((node) => { node.hidden = !settings.seconds; });
      period.hidden = settings.format !== "12h" || !settings.showPeriod;
      period.textContent = values.A;
    }
    display.dateTime = now.toISOString();
    display.dataset.webskinClockFormat = settings.format;
  };
  const syncTime = async () => {
    if (!hasAetherBridge(renderer)) return;
    try {
      const epoch = await readNyxTime(renderer);
      if (Number.isFinite(epoch)) epochOffset = epoch - Date.now();
    } catch {
    }
  };
  render();
  void syncTime();
  const renderTimer = window.setInterval(render, settings.updateInterval * 1000);
  const syncTimer = window.setInterval(syncTime, 60_000);
  onPageCleanup(root, () => {
    window.clearInterval(renderTimer);
    window.clearInterval(syncTimer);
  });
  return { destroy: () => destroyWidget(root) };
}

if (globalThis.__KHAOSSHELL_WIDGET_AUTOSTART__) mount(globalThis.document);
