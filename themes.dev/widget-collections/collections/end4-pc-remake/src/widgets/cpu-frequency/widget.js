import { destroyWidget, onPageCleanup } from "@khaosshell/widget-runtime/lifecycle.js";
import { applyWidgetTheme, readWidgetTheme } from "@khaosshell/widget-runtime/theme.js";
import { formatMetricNumber, formatMetricTemplate, isMetricValueAvailable, metricSettings, startNyxMetricWidget } from "@khaosshell/widget-runtime/metrics.js";
import { integerSetting, resolveSetting, stringSetting, tokenFormatSetting, widgetContext } from "@khaosshell/widget-runtime/settings.js";

const TOKENS = ["curr", "current", "min", "minimum", "max", "maximum", "core", "zeroed-core"];

const SCALE = 1;

export function mount(root = globalThis.document, options = {}) {
  if (!root) throw new Error("KhaosShell widget mount requires a root element or document.");
  const { document, window, renderer } = widgetContext(root, options);
  const WIDGET_ID = "cpu-frequency";
  applyWidgetTheme(document, readWidgetTheme(WIDGET_ID, { primary: "#d6d6d6", secondary: "#2e2e2e", scale: SCALE }, renderer));
  const core = resolveSetting({ root, widgetId: WIDGET_ID, options, renderer, key: "core", defaultValue: -1, validate: integerSetting({ minimum: -1 }) });
  const digits = resolveSetting({ root, widgetId: WIDGET_ID, options, renderer, key: "digits", defaultValue: 0, validate: integerSetting({ minimum: 0, maximum: 3 }) });
  const format = resolveSetting({ root, widgetId: WIDGET_ID, options, renderer, key: "format", defaultValue: core < 0 ? "{current} MHz" : "Core {core}: {current} MHz", validate: tokenFormatSetting(TOKENS) });
  const digitLimit = resolveSetting({ root, widgetId: WIDGET_ID, options, renderer, key: "digitLimit", defaultValue: 5, validate: integerSetting({ minimum: 1 }) });
  const label = resolveSetting({ root, widgetId: WIDGET_ID, options, renderer, key: "label", defaultValue: "CPU", validate: stringSetting() });
  const { updateInterval = 30 } = metricSettings({ root, widgetId: WIDGET_ID, options, renderer, keys: ["updateInterval"], defaults: { updateInterval: 30 } });
  const valueNode = document.querySelector('[data-webskin-frame-part="value"]');
  const subtextNode = document.querySelector('[data-webskin-frame-part="subtext"]');
  subtextNode.textContent = label;
  const render = (metrics, phase) => {
    const frequencies = phase === "preview" ? { current_mhz: 3400, minimum_mhz: 800, maximum_mhz: 4800, per_core: [] } : metrics?.cpu?.frequencies;
    const selected = core < 0 ? frequencies : frequencies?.per_core?.[core];
    const value = (key, fallback) => formatMetricNumber(selected?.[key] ?? (core < 0 ? frequencies?.[key] : undefined), digits, digitLimit) || fallback;
    valueNode.textContent = formatMetricTemplate(format, {
      curr: value("current_mhz", "--"),
      current: value("current_mhz", "--"),
      min: value("minimum_mhz", "--"),
      minimum: value("minimum_mhz", "--"),
      max: value("maximum_mhz", "--"),
      maximum: value("maximum_mhz", "--"),
      core: core < 0 ? "-1" : String(core + 1),
      "zeroed-core": String(core),
    }, !isMetricValueAvailable(selected?.current_mhz));
  };
  const fields = core < 0 ? ["cpu.frequencies"] : ["cpu.frequencies.per_core"];
  const close = startNyxMetricWidget({ root, window, renderer, widgetId: WIDGET_ID, fields, interval: updateInterval, render, preview: null, onError: () => { valueNode.textContent = "--"; } });
  onPageCleanup(root, () => close());
  return { destroy: () => destroyWidget(root) };
}

if (globalThis.__KHAOSSHELL_WIDGET_AUTOSTART__) mount(globalThis.document);
