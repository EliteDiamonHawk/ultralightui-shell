import { hasAetherBridge, readNyxTime } from "@khaosshell/widget-runtime/bridge.js";
import { dateKey, formatDatePattern, resolveCalendarSettings } from "@khaosshell/widget-runtime/calendar.js";
import { destroyWidget, onPageCleanup } from "@khaosshell/widget-runtime/lifecycle.js";
import { applyWidgetTheme, readWidgetTheme } from "@khaosshell/widget-runtime/theme.js";
import { resolveSetting, stringSetting, widgetContext } from "@khaosshell/widget-runtime/settings.js";

const SCALE = 1;

export function mount(root = globalThis.document, options = {}) {
  if (!root) throw new Error("KhaosShell widget mount requires a root element or document.");
  const { document, window, renderer } = widgetContext(root, options);
  const WIDGET_ID = "single-line-date";
  applyWidgetTheme(document, readWidgetTheme(WIDGET_ID, { primary: "#d6d6d6", secondary: "#2e2e2e", scale: SCALE }, renderer));
  let epochOffset = 0;
  const settings = resolveCalendarSettings({ root, widgetId: WIDGET_ID, options, renderer, kind: "date", now: new Date() });
  const format = resolveSetting({ root, widgetId: WIDGET_ID, options, renderer, key: "format", defaultValue: undefined, validate: stringSetting() });
  const dateNode = document.querySelector("time");
  const render = () => {
    const current = settings.date ? settings.anchor : new Date(Date.now() + epochOffset);
    const date = settings.date ? settings.anchor : new Date(current.getFullYear(), current.getMonth(), current.getDate(), 12);
    const highlight = settings.highlightDate ? settings.highlight : date;
    if (format) {
      dateNode.textContent = formatDatePattern(date, format, settings.formatters);
    } else {
      const parts = settings.formatters.medium.formatToParts(date);
      dateNode.querySelector('[data-webskin-calendar-part="month"]').textContent = parts.find((part) => part.type === "month")?.value ?? "";
      dateNode.querySelector('[data-webskin-calendar-part="day"]').textContent = parts.find((part) => part.type === "day")?.value ?? "";
      dateNode.querySelector('[data-webskin-calendar-part="year"]').textContent = parts.find((part) => part.type === "year")?.value ?? "";
    }
    dateNode.dateTime = dateKey(date);
    dateNode.dataset.webskinCalendarDate = dateKey(date);
    dateNode.dataset.webskinCalendarToday = String(dateKey(date) === dateKey(highlight));
    dateNode.dataset.webskinCalendarLocale = settings.locale;
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
