import { hasAetherBridge, readNyxTime } from "@khaosshell/widget-runtime/bridge.js";
import { addDays, dateKey, resolveCalendarSettings, rollingWeekDays, weekDays } from "@khaosshell/widget-runtime/calendar.js";
import { destroyWidget, onPageCleanup } from "@khaosshell/widget-runtime/lifecycle.js";
import { applyWidgetTheme, readWidgetTheme } from "@khaosshell/widget-runtime/theme.js";
import { widgetContext } from "@khaosshell/widget-runtime/settings.js";

const SCALE = 1;

export function mount(root = globalThis.document, options = {}) {
  if (!root) throw new Error("KhaosShell widget mount requires a root element or document.");
  const { document, window, renderer } = widgetContext(root, options);
  const WIDGET_ID = "week";
  applyWidgetTheme(document, readWidgetTheme(WIDGET_ID, { primary: "#d6d6d6", secondary: "#2e2e2e", scale: SCALE }, renderer));
  let epochOffset = 0;
  const settings = resolveCalendarSettings({ root, widgetId: WIDGET_ID, options, renderer, kind: "week", now: new Date() });
  const cells = [...document.querySelectorAll('[data-webskin-calendar-part="date-cell"]')];
  const render = () => {
    const current = settings.date ? settings.anchor : new Date(Date.now() + epochOffset);
    const anchor = settings.date ? settings.anchor : new Date(current.getFullYear(), current.getMonth(), current.getDate(), 12);
    const highlight = settings.highlightDate ? settings.highlight : anchor;
    const days = settings.daysBefore === undefined ? weekDays(anchor, settings.weekStartsOn) : rollingWeekDays(anchor, settings.daysBefore);
    const start = days[0];
    cells.forEach((cell, index) => {
      const date = days[index];
      const highlighted = dateKey(date) === dateKey(highlight);
      cell.dateTime = dateKey(date);
      cell.dataset.webskinCalendarDate = dateKey(date);
      cell.dataset.webskinCalendarWeekday = settings.formatters.weekdayShort.format(date);
      cell.dataset.webskinCalendarWeekdayIndex = String(date.getDay());
      cell.dataset.webskinCalendarToday = String(highlighted);
      cell.classList.toggle("is-today", highlighted);
      const weekday = cell.querySelector('[data-webskin-calendar-part="weekday"]');
      weekday.textContent = settings.formatters.weekdayShort.format(date);
      weekday.dataset.webskinCalendarToday = String(highlighted);
      cell.querySelector('[data-webskin-calendar-part="day"]').textContent = String(date.getDate());
      cell.querySelector('[data-webskin-calendar-part="month"]').textContent = settings.formatters.monthShort.format(date);
    });
    document.documentElement.dataset.webskinCalendarLocale = settings.locale;
    document.documentElement.dataset.webskinCalendarDate = dateKey(anchor);
    document.documentElement.dataset.webskinCalendarStartDate = dateKey(start);
    document.documentElement.dataset.webskinCalendarEndDate = dateKey(addDays(start, 6));
    document.documentElement.dataset.webskinCalendarWeekStartsOn = String(settings.weekStartsOn);
    document.documentElement.dataset.webskinCalendarDaysBefore = settings.daysBefore === undefined ? "" : String(settings.daysBefore);
    document.documentElement.dataset.webskinCalendarDaysAfter = settings.daysAfter === undefined ? "" : String(settings.daysAfter);
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
