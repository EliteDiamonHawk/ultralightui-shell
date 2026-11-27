import { getAetherVariable } from "./bridge.js";

const COLLECTION_ID = "end4-pc-remake";

const hasOwn = (value, key) => Object.prototype.hasOwnProperty.call(value ?? {}, key);

const settingKey = (widgetId, key) => `widget.${COLLECTION_ID}.${widgetId}.${key}`;

export const markSettingWarning = (root, key) => {
  const element = root?.documentElement ?? root?.ownerDocument?.documentElement;
  if (!element) return;
  const current = new Set(String(element.dataset.widgetConfigWarning ?? "").split(",").filter(Boolean));
  current.add(key);
  element.dataset.widgetConfigWarning = [...current].join(",");
};

export const settingIsExplicit = (options, key) => hasOwn(options, key) && options[key] !== undefined;

export const settingError = (key, message) => {
  const error = new RangeError(`${key}: ${message}`);
  error.settingKey = key;
  return error;
};

export const resolveSetting = ({ root, widgetId, key, options = {}, renderer, defaultValue, validate }) => {
  const explicit = settingIsExplicit(options, key);
  const configured = explicit
    ? options[key]
    : getAetherVariable(settingKey(widgetId, key), undefined, renderer);
  if (configured === undefined) return defaultValue;
  try {
    return validate ? validate(configured, key) : configured;
  } catch (error) {
    if (explicit) throw error;
    markSettingWarning(root, key);
    return defaultValue;
  }
};

export const stringSetting = (fallback, { allowEmpty = false } = {}) => (value, key) => {
  if (typeof value !== "string" || (!allowEmpty && !value.trim())) {
    throw settingError(key, "must be a non-empty string");
  }
  return value;
};

export const enumSetting = (values, fallback) => (value, key) => {
  if (!values.includes(value)) throw settingError(key, `must be one of ${values.join(", ")}`);
  return value;
};

export const integerSetting = ({ minimum, maximum, fallback } = {}) => (value, key) => {
  if (!Number.isInteger(value)) throw settingError(key, "must be an integer");
  if (minimum !== undefined && value < minimum) throw settingError(key, `must be at least ${minimum}`);
  if (maximum !== undefined && value > maximum) throw settingError(key, `must be at most ${maximum}`);
  return value;
};

export const numberSetting = ({ minimum, maximum } = {}) => (value, key) => {
  if (typeof value !== "number" || !Number.isFinite(value)) throw settingError(key, "must be a finite number");
  if (minimum !== undefined && value < minimum) throw settingError(key, `must be at least ${minimum}`);
  if (maximum !== undefined && value > maximum) throw settingError(key, `must be at most ${maximum}`);
  return value;
};

export const booleanSetting = (value, key) => {
  if (typeof value !== "boolean") throw settingError(key, "must be boolean");
  return value;
};

export const dateSetting = (value, key) => {
  if (value === undefined || value === null) return undefined;
  if (typeof value !== "string" || !/^\d{4}-\d{2}-\d{2}$/.test(value)) {
    throw settingError(key, "must use YYYY-MM-DD");
  }
  const [, year, month, day] = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value);
  const parsed = new Date(0);
  parsed.setUTCFullYear(Number(year), Number(month) - 1, Number(day));
  parsed.setUTCHours(0, 0, 0, 0);
  if (parsed.getUTCFullYear() !== Number(year) || parsed.getUTCMonth() !== Number(month) - 1 || parsed.getUTCDate() !== Number(day)) {
    throw settingError(key, "must be a real calendar date");
  }
  return value;
};

export const positiveIntervalSetting = integerSetting({ minimum: 1 });
export const nyxIntervalSetting = integerSetting({ minimum: 1, maximum: 3600 });

export const tokenFormatSetting = (tokens) => (value, key) => {
  if (typeof value !== "string") throw settingError(key, "must be a string");
  const supported = new Set(tokens);
  for (const match of value.matchAll(/\{([a-z][a-z0-9-]*)\}/g)) {
    if (!supported.has(match[1])) throw settingError(key, `contains unsupported token {${match[1]}}`);
  }
  return value;
};

export const readWidgetSettings = (root, widgetId, options, renderer, descriptors) => Object.fromEntries(
  Object.entries(descriptors).map(([key, descriptor]) => [
    key,
    resolveSetting({ root, widgetId, options, renderer, key, defaultValue: descriptor.default, validate: descriptor.validate }),
  ]),
);

export const widgetContext = (root, options = {}) => {
  const pageDocument = root?.ownerDocument ?? root ?? globalThis.document;
  const queryRoot = root?.querySelector ? root : pageDocument;
  const document = new Proxy(pageDocument, {
    get(target, property, receiver) {
      if (property === "querySelector") return queryRoot.querySelector.bind(queryRoot);
      if (property === "querySelectorAll") return queryRoot.querySelectorAll.bind(queryRoot);
      const value = Reflect.get(target, property, receiver);
      return typeof value === "function" ? value.bind(target) : value;
    },
  });
  const window = options.window ?? pageDocument.defaultView ?? globalThis.window;
  return { pageDocument, document, window, renderer: window?.renderer };
};

