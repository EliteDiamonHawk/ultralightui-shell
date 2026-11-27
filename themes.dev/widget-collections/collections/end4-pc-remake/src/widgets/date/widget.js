import { hasAetherBridge, readNyxTime } from "@khaosshell/widget-runtime/bridge.js";
import { dateKey, resolveCalendarSettings } from "@khaosshell/widget-runtime/calendar.js";
import { destroyWidget, onPageCleanup } from "@khaosshell/widget-runtime/lifecycle.js";
import { applyWidgetTheme, readWidgetTheme } from "@khaosshell/widget-runtime/theme.js";
import { widgetContext } from "@khaosshell/widget-runtime/settings.js";

const SCALE = 1;

export function mount(root = globalThis.document, options = {}) {
  if (!root) throw new Error("KhaosShell widget mount requires a root element or document.");
  const { document, window, renderer } = widgetContext(root, options);
  const WIDGET_ID = "date";
  applyWidgetTheme(document, readWidgetTheme(WIDGET_ID, { primary: "#d6d6d6", secondary: "#2e2e2e", scale: SCALE }, renderer));
  let epochOffset = 0;
  const settings = resolveCalendarSettings({ root, widgetId: WIDGET_ID, options, renderer, kind: "date", now: new Date() });
  const weekday = document.querySelector('[data-webskin-calendar-part="weekday"]');
  const day = document.querySelector('[data-webskin-calendar-part="day"]');
  const month = document.querySelector('[data-webskin-calendar-part="month"]');
  const year = document.querySelector('[data-webskin-calendar-part="year"]');
  const render = () => {
    const current = settings.date ? settings.anchor : new Date(Date.now() + epochOffset);
    const date = settings.date ? settings.anchor : new Date(current.getFullYear(), current.getMonth(), current.getDate(), 12);
    const highlight = settings.highlightDate ? settings.highlight : date;
    weekday.textContent = settings.formatters.weekdayLong.format(date);
    day.textContent = String(date.getDate());
    month.textContent = settings.formatters.monthLong.format(date);
    year.textContent = String(date.getFullYear());
    document.documentElement.dataset.webskinCalendarDate = dateKey(date);
    document.documentElement.dataset.webskinCalendarToday = String(dateKey(date) === dateKey(highlight));
    document.documentElement.dataset.webskinCalendarLocale = settings.locale;
    document.documentElement.dataset.webskinCalendarWeekday = String(date.getDay());
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
