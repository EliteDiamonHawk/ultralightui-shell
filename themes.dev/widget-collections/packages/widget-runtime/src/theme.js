import { getAetherVariable } from "./bridge.js";

const COLLECTION_ID = "end4-pc-remake";
const WIDGET_VARIABLE_PREFIX = `widget.${COLLECTION_ID}`;

const isCssColor = (value) => {
  if (typeof value !== "string" || !value.trim()) return false;
  if (typeof globalThis.CSS?.supports !== "function") return true;
  try {
    return globalThis.CSS.supports("color", value.trim());
  } catch {
    return false;
  }
};

const readColor = (widgetId, role, fallback, explicitRenderer) => {
  const widgetValue = getAetherVariable(`${WIDGET_VARIABLE_PREFIX}.${widgetId}.${role}`, undefined, explicitRenderer);
  if (isCssColor(widgetValue)) return widgetValue.trim();

  const collectionValue = getAetherVariable(`widget-collection.${COLLECTION_ID}.${role}`, undefined, explicitRenderer);
  if (isCssColor(collectionValue)) return collectionValue.trim();

  return fallback;
};

export function readWidgetTheme(widgetId, defaults, explicitRenderer) {
  const configuredScale = Number(getAetherVariable(`${WIDGET_VARIABLE_PREFIX}.${widgetId}.scale`, undefined, explicitRenderer));
  const scale = Number.isFinite(configuredScale) && configuredScale > 0
    ? configuredScale
    : defaults.scale;

  return Object.freeze({
    primary: readColor(widgetId, "primary", defaults.primary, explicitRenderer),
    secondary: readColor(widgetId, "secondary", defaults.secondary, explicitRenderer),
    scale,
  });
}

export function applyWidgetTheme(document, theme) {
  document.documentElement.style.setProperty("--widget-primary", theme.primary);
  document.documentElement.style.setProperty("--widget-secondary", theme.secondary);
  document.documentElement.style.setProperty("--widget-scale", String(theme.scale));
}
