import { hasAetherBridge, readNyxTime } from "@khaosshell/widget-runtime/bridge.js";
import { dateKey, monthGrid, resolveCalendarSettings, rollingMonthGrid, weekdayName } from "@khaosshell/widget-runtime/calendar.js";
import { destroyWidget, onPageCleanup } from "@khaosshell/widget-runtime/lifecycle.js";
import { applyWidgetTheme, readWidgetTheme } from "@khaosshell/widget-runtime/theme.js";
import { widgetContext } from "@khaosshell/widget-runtime/settings.js";

const SCALE = 1;

export function mount(root = globalThis.document, options = {}) {
  if (!root) throw new Error("KhaosShell widget mount requires a root element or document.");
  const { document, window, renderer } = widgetContext(root, options);
  const WIDGET_ID = "month";
  applyWidgetTheme(document, readWidgetTheme(WIDGET_ID, { primary: "#d6d6d6", secondary: "#2e2e2e", scale: SCALE }, renderer));
  let epochOffset = 0;
  const settings = resolveCalendarSettings({ root, widgetId: WIDGET_ID, options, renderer, kind: "month", now: new Date() });
  const monthNode = document.querySelector('[data-webskin-calendar-part="month"]');
  const yearNode = document.querySelector('[data-webskin-calendar-part="year"]');
  const headings = [...document.querySelectorAll('[data-webskin-calendar-part="weekday-heading"]')];
  const cells = [...document.querySelectorAll('[data-webskin-calendar-part="date-cell"]')];
  const render = () => {
    const current = settings.date ? settings.anchor : new Date(Date.now() + epochOffset);
    const anchor = settings.date ? settings.anchor : new Date(current.getFullYear(), current.getMonth(), current.getDate(), 12);
    const highlight = settings.highlightDate ? settings.highlight : anchor;
    const grid = settings.lockDay
      ? rollingMonthGrid(anchor, settings.weekStartsOn, settings.lockDay, settings.maxWeeks)
      : monthGrid(anchor, settings.weekStartsOn, settings.maxWeeks);
    const headingStart = grid[0].date;
    monthNode.textContent = settings.formatters.monthLong.format(anchor);
    yearNode.textContent = String(anchor.getFullYear());
    headings.forEach((heading, index) => {
      const headingDate = new Date(headingStart);
      headingDate.setDate(headingDate.getDate() + index);
      const weekdayIndex = headingDate.getDay();
      heading.textContent = weekdayName(weekdayIndex, settings.formatters, "short");
      heading.dataset.webskinCalendarWeekday = ["sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday"][weekdayIndex];
      heading.dataset.webskinCalendarWeekdayIndex = String(weekdayIndex);
    });
    cells.forEach((cell, index) => {
      const visible = index < grid.length;
      cell.hidden = !visible;
      if (!visible) return;
      const item = grid[index];
      const highlighted = dateKey(item.date) === dateKey(highlight);
      cell.dateTime = dateKey(item.date);
      cell.dataset.webskinCalendarDate = dateKey(item.date);
      cell.dataset.webskinCalendarInMonth = String(item.inMonth);
      cell.dataset.webskinCalendarToday = String(highlighted);
      cell.dataset.webskinCalendarWeekday = ["sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday"][item.date.getDay()];
      cell.dataset.webskinCalendarWeekdayIndex = String(item.date.getDay());
      cell.classList.toggle("is-adjacent", !item.inMonth);
      cell.classList.toggle("is-today", highlighted);
      cell.querySelector('[data-webskin-calendar-part="day"]').textContent = String(item.date.getDate());
    });
    document.documentElement.dataset.webskinCalendarLocale = settings.locale;
    document.documentElement.dataset.webskinCalendarDate = dateKey(anchor);
    document.documentElement.dataset.webskinCalendarMonth = dateKey(anchor).slice(0, 7);
    document.documentElement.dataset.webskinCalendarWeekStartsOn = String(settings.weekStartsOn);
    document.documentElement.dataset.webskinCalendarMaxWeeks = String(settings.maxWeeks);
    document.documentElement.style.setProperty("--webskin-calendar-weeks", String(settings.maxWeeks));
    if (settings.lockDay) {
      document.documentElement.dataset.webskinCalendarLockY = String(settings.lockDay.y);
      if (settings.lockDay.x === undefined) delete document.documentElement.dataset.webskinCalendarLockX;
      else document.documentElement.dataset.webskinCalendarLockX = String(settings.lockDay.x);
    } else {
      delete document.documentElement.dataset.webskinCalendarLockY;
      delete document.documentElement.dataset.webskinCalendarLockX;
    }
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
