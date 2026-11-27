import { dateSetting, integerSetting, markSettingWarning, resolveSetting, settingError, settingIsExplicit, stringSetting, positiveIntervalSetting } from "./settings.js";

const DAY_NAMES = ["sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday"];
const DATE_TOKENS = /YYYY|MMMM|dddd|MMM|ddd|YY|MM|DD|M|D/g;

export const dateKey = (date) => `${String(date.getFullYear()).padStart(4, "0")}-${String(date.getMonth() + 1).padStart(2, "0")}-${String(date.getDate()).padStart(2, "0")}`;

export const createLocalDate = (year, month, day) => {
  const date = new Date(2000, 0, 1, 12);
  date.setFullYear(year, month, day);
  if (date.getFullYear() !== year || date.getMonth() !== month || date.getDate() !== day) {
    throw new RangeError("Invalid calendar date");
  }
  return date;
};

export const normalizeDate = (value, fallback = new Date()) => {
  if (value === undefined || value === null) return createLocalDate(fallback.getFullYear(), fallback.getMonth(), fallback.getDate());
  if (typeof value !== "string" || !/^\d{4}-\d{2}-\d{2}$/.test(value)) throw new TypeError("Calendar dates must use YYYY-MM-DD");
  const [, year, month, day] = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value);
  return createLocalDate(Number(year), Number(month) - 1, Number(day));
};

export const addDays = (date, amount) => {
  const result = new Date(date);
  result.setDate(result.getDate() + amount);
  result.setHours(12, 0, 0, 0);
  return result;
};

export const sameDate = (left, right) => dateKey(left) === dateKey(right);

export const resolveLocale = (locale) => {
  if (locale === undefined || locale === null) return new Intl.DateTimeFormat().resolvedOptions().locale;
  new Intl.DateTimeFormat(locale);
  return locale;
};

export const resolveWeekStartsOn = (locale, configured) => {
  if (configured !== undefined && configured !== null) return integerSetting({ minimum: 0, maximum: 6 })(configured, "weekStartsOn");
  try {
    const localeObject = new Intl.Locale(locale);
    const weekInfo = localeObject.weekInfo ?? localeObject.getWeekInfo?.();
    if (weekInfo?.firstDay >= 1 && weekInfo.firstDay <= 7) return weekInfo.firstDay % 7;
  } catch {
  }
  return 0;
};

export const formattersFor = (locale) => ({
  weekdayLong: new Intl.DateTimeFormat(locale, { weekday: "long" }),
  weekdayShort: new Intl.DateTimeFormat(locale, { weekday: "short" }),
  monthLong: new Intl.DateTimeFormat(locale, { month: "long" }),
  monthShort: new Intl.DateTimeFormat(locale, { month: "short" }),
  medium: new Intl.DateTimeFormat(locale, { dateStyle: "medium" }),
});

const weekdayDate = (index) => createLocalDate(2023, 0, index + 1);

export const weekdayName = (index, formatters, style = "short") => (
  (style === "long" ? formatters.weekdayLong : formatters.weekdayShort).format(weekdayDate(index))
);

export const monthName = (date, formatters, style = "short") => (
  (style === "long" ? formatters.monthLong : formatters.monthShort).format(date)
);

export const startOfWeek = (date, weekStartsOn) => addDays(date, -((date.getDay() - weekStartsOn + 7) % 7));

export const weekDays = (date, weekStartsOn) => {
  const start = startOfWeek(date, weekStartsOn);
  return Array.from({ length: 7 }, (_, index) => addDays(start, index));
};

export const rollingWeekDays = (date, daysBefore) => {
  const start = addDays(date, -daysBefore);
  return Array.from({ length: 7 }, (_, index) => addDays(start, index));
};

export const monthGrid = (date, weekStartsOn, maxWeeks) => {
  const monthStart = createLocalDate(date.getFullYear(), date.getMonth(), 1);
  const monthEnd = new Date(date);
  monthEnd.setDate(1);
  monthEnd.setMonth(monthEnd.getMonth() + 1, 0);
  monthEnd.setHours(12, 0, 0, 0);
  const gridStart = startOfWeek(monthStart, weekStartsOn);
  const leadingDays = (monthStart.getDay() - weekStartsOn + 7) % 7;
  const naturalWeeks = Math.ceil((monthEnd.getDate() + leadingDays) / 7);
  const anchorWeek = Math.floor((leadingDays + date.getDate() - 1) / 7);
  const firstWeek = naturalWeeks > maxWeeks && anchorWeek >= maxWeeks - 1
    ? Math.min(naturalWeeks - maxWeeks, anchorWeek)
    : 0;
  const start = addDays(gridStart, firstWeek * 7);
  return Array.from({ length: maxWeeks * 7 }, (_, index) => {
    const cellDate = addDays(start, index);
    return { date: cellDate, inMonth: cellDate.getMonth() === date.getMonth() && cellDate.getFullYear() === date.getFullYear() };
  });
};

export const rollingMonthGrid = (date, weekStartsOn, lockDay, maxWeeks) => {
  const column = (date.getDay() - weekStartsOn + 7) % 7;
  const start = addDays(date, -(lockDay.y * 7 + (lockDay.x ?? column)));
  return Array.from({ length: maxWeeks * 7 }, (_, index) => {
    const cellDate = addDays(start, index);
    return { date: cellDate, inMonth: cellDate.getMonth() === date.getMonth() && cellDate.getFullYear() === date.getFullYear() };
  });
};

export const resolveCalendarSettings = ({ root, widgetId, options, renderer, kind, now }) => {
  const descriptors = {
    date: { default: undefined, validate: dateSetting },
    locale: { default: "en-US", validate: (value, key) => { const valid = stringSetting()(value, key); new Intl.DateTimeFormat(valid); return valid; } },
    weekStartsOn: { default: undefined, validate: integerSetting({ minimum: 0, maximum: 6 }) },
    highlightDate: { default: undefined, validate: dateSetting },
    updateInterval: { default: kind === "clock" ? 5 : 60, validate: positiveIntervalSetting },
  };
  if (kind === "week") {
    descriptors.daysBefore = { default: undefined, validate: integerSetting({ minimum: 0, maximum: 6 }) };
    descriptors.daysAfter = { default: undefined, validate: integerSetting({ minimum: 0, maximum: 6 }) };
  }
  if (kind === "month") {
    descriptors.maxWeeks = { default: 6, validate: integerSetting({ minimum: 1, maximum: 6 }) };
    descriptors.lockDay = { default: undefined, validate: (value, key) => {
      if (!value || typeof value !== "object" || Array.isArray(value) || !Number.isInteger(value.y)) throw new TypeError(`${key} must contain integer y`);
      if (value.y < 0 || value.y >= 6 || (value.x !== undefined && (!Number.isInteger(value.x) || value.x < 0 || value.x > 6))) throw new RangeError(`${key} is outside the calendar grid`);
      return { x: value.x, y: value.y };
    } };
  }
  const settings = Object.fromEntries(Object.entries(descriptors).map(([key, descriptor]) => [
    key,
    resolveSetting({ root, widgetId, options, renderer, key, defaultValue: descriptor.default, validate: descriptor.validate }),
  ]));
  const locale = resolveLocale(settings.locale);
  const weekStartsOn = resolveWeekStartsOn(locale, settings.weekStartsOn);
  const anchor = normalizeDate(settings.date, now);
  const highlight = normalizeDate(settings.highlightDate, settings.date === undefined ? now : anchor);
  if (kind === "week") {
    const before = settings.daysBefore;
    const after = settings.daysAfter;
    if (before !== undefined || after !== undefined) {
      if (before !== undefined && after !== undefined && before + after !== 6) {
        if (settingIsExplicit(options, "daysBefore") || settingIsExplicit(options, "daysAfter")) {
          throw settingError("daysBefore/daysAfter", "must total 6");
        }
        markSettingWarning(root, "daysBefore/daysAfter");
        settings.daysBefore = undefined;
        settings.daysAfter = undefined;
      } else {
        settings.daysBefore = before ?? 6 - after;
        settings.daysAfter = after ?? 6 - before;
      }
    }
  }
  if (kind === "month" && settings.lockDay && settings.lockDay.y >= settings.maxWeeks) {
    if (settingIsExplicit(options, "lockDay") || settingIsExplicit(options, "maxWeeks")) {
      throw settingError("lockDay/maxWeeks", "lockDay.y must be smaller than maxWeeks");
    }
    markSettingWarning(root, "lockDay/maxWeeks");
    settings.lockDay = undefined;
  }
  return { ...settings, locale, weekStartsOn, anchor, highlight, formatters: formattersFor(locale) };
};

export const formatDatePattern = (date, pattern, formatters) => {
  const values = {
    YYYY: String(date.getFullYear()).padStart(4, "0"),
    YY: String(date.getFullYear() % 100).padStart(2, "0"),
    MMMM: monthName(date, formatters, "long"),
    MMM: monthName(date, formatters, "short"),
    MM: String(date.getMonth() + 1).padStart(2, "0"),
    M: String(date.getMonth() + 1),
    DD: String(date.getDate()).padStart(2, "0"),
    D: String(date.getDate()),
    dddd: weekdayName(date.getDay(), formatters, "long"),
    ddd: weekdayName(date.getDay(), formatters, "short"),
  };
  return pattern.replace(DATE_TOKENS, (token) => values[token] ?? token);
};

