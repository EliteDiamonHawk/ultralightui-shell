import { destroyWidget, onPageCleanup } from "@khaosshell/widget-runtime/lifecycle.js";
import { applyWidgetTheme, readWidgetTheme } from "@khaosshell/widget-runtime/theme.js";
import { formatMetricNumber, formatMetricTemplate, isMetricValueAvailable, metricSettings, startNyxMetricWidget } from "@khaosshell/widget-runtime/metrics.js";
import { enumSetting, integerSetting, resolveSetting, stringSetting, tokenFormatSetting, widgetContext } from "@khaosshell/widget-runtime/settings.js";

const SCALE = 1;

export function mount(root = globalThis.document, options = {}) {
  if (!root) throw new Error("KhaosShell widget mount requires a root element or document.");
  const { document, window, renderer } = widgetContext(root, options);
  const WIDGET_ID = "cpu-core-count";
  applyWidgetTheme(document, readWidgetTheme(WIDGET_ID, { primary: "#d6d6d6", secondary: "#2e2e2e", scale: SCALE }, renderer));
  const coreType = resolveSetting({ root, widgetId: WIDGET_ID, options, renderer, key: "coreType", defaultValue: "logical", validate: enumSetting(["physical", "logical"]) });
  const format = resolveSetting({ root, widgetId: WIDGET_ID, options, renderer, key: "format", defaultValue: `{${coreType}}`, validate: tokenFormatSetting(["physical", "phys", "logical", "logi"]) });
  const digitLimit = resolveSetting({ root, widgetId: WIDGET_ID, options, renderer, key: "digitLimit", defaultValue: 5, validate: integerSetting({ minimum: 1 }) });
  const label = resolveSetting({ root, widgetId: WIDGET_ID, options, renderer, key: "label", defaultValue: coreType === "physical" ? "Physical cores" : "Logical cores", validate: stringSetting() });
  const { updateInterval = 60 } = metricSettings({ root, widgetId: WIDGET_ID, options, renderer, keys: ["updateInterval"], defaults: { updateInterval: 60 } });
  const valueNode = document.querySelector('[data-webskin-frame-part="value"]');
  const subtextNode = document.querySelector('[data-webskin-frame-part="subtext"]');
  subtextNode.textContent = label;
  const render = (metrics, phase) => {
    const cores = phase === "preview" ? { physical: 4, logical: 8 } : metrics?.cpu?.cores;
    valueNode.textContent = formatMetricTemplate(format, {
      physical: formatMetricNumber(cores?.physical, 0, digitLimit),
      phys: formatMetricNumber(cores?.physical, 0, digitLimit),
      logical: formatMetricNumber(cores?.logical, 0, digitLimit),
      logi: formatMetricNumber(cores?.logical, 0, digitLimit),
    }, !isMetricValueAvailable(cores?.[coreType]));
  };
  const close = startNyxMetricWidget({ root, window, renderer, widgetId: WIDGET_ID, fields: ["cpu.cores"], interval: updateInterval, render, preview: null, onError: () => { valueNode.textContent = "--"; } });
  onPageCleanup(root, () => close());
  return { destroy: () => destroyWidget(root) };
}

if (globalThis.__KHAOSSHELL_WIDGET_AUTOSTART__) mount(globalThis.document);
