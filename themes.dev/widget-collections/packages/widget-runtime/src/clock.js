import { resolveSetting, enumSetting, booleanSetting, numberSetting, positiveIntervalSetting, stringSetting } from "./settings.js";

export const resolveClockSettings = ({ root, widgetId, options, renderer, defaults = {} }) => {
  const read = (key, defaultValue, validate) => resolveSetting({ root, widgetId, options, renderer, key, defaultValue, validate });
  return {
    format: read("format", defaults.format ?? "12h", enumSetting(["12h", "24h"])),
    seconds: read("seconds", defaults.seconds ?? true, booleanSetting),
    showPeriod: read("showPeriod", defaults.showPeriod ?? true, booleanSetting),
    syntax: read("syntax", defaults.syntax, stringSetting()),
    offsetMinutes: read("offsetMinutes", defaults.offsetMinutes ?? 0, numberSetting()),
    updateInterval: read("updateInterval", defaults.updateInterval ?? 5, positiveIntervalSetting),
    smooth: read("smooth", defaults.smooth ?? false, booleanSetting),
  };
};

export const shiftedDate = (date, offsetMinutes) => new Date(date.getTime() + Number(offsetMinutes) * 60_000);

export const pad2 = (value) => String(value).padStart(2, "0");

export const digitalValues = (date, format) => {
  const hour24 = date.getHours();
  const hour12 = hour24 % 12 || 12;
  return {
    HH: pad2(hour24),
    H: String(hour24),
    hh: pad2(hour12),
    h: String(hour12),
    mm: pad2(date.getMinutes()),
    m: String(date.getMinutes()),
    ss: pad2(date.getSeconds()),
    s: String(date.getSeconds()),
    A: hour24 >= 12 ? "PM" : "AM",
    a: hour24 >= 12 ? "pm" : "am",
    format,
  };
};

export const defaultDigitalSyntax = ({ format, seconds, showPeriod }) => [
  format === "24h" ? "HH" : "hh",
  ":",
  "mm",
  seconds ? ":ss" : "",
  format === "12h" && showPeriod ? " A" : "",
].join("");

export const renderDigitalSyntax = (syntax, values) => String(syntax).replace(/HH|hh|mm|ss|H|h|m|s|A|a/g, (token) => values[token] ?? token);

export const clockAngles = (date, smooth) => ({
  hour: ((date.getHours() % 12) + date.getMinutes() / 60 + (smooth ? date.getSeconds() / 3600 : 0)) * 30,
  minute: (date.getMinutes() + (smooth ? date.getSeconds() / 60 : 0)) * 6,
  second: (date.getSeconds() + (smooth ? date.getMilliseconds() / 1000 : 0)) * 6,
});

