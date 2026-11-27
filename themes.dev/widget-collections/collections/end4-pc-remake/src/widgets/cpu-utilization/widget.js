import { destroyWidget, onPageCleanup } from "@khaosshell/widget-runtime/lifecycle.js";
import { applyWidgetTheme, readWidgetTheme } from "@khaosshell/widget-runtime/theme.js";
import { formatMetricNumber, formatMetricTemplate, metricSettings, startNyxMetricWidget } from "@khaosshell/widget-runtime/metrics.js";
import { integerSetting, resolveSetting, stringSetting, tokenFormatSetting, widgetContext } from "@khaosshell/widget-runtime/settings.js";

const SCALE = 1;

export function mount(root = globalThis.document, options = {}) {
  if (!root) throw new Error("KhaosShell widget mount requires a root element or document.");
  const { document, window, renderer } = widgetContext(root, options);
  const WIDGET_ID = "cpu-utilization";
  applyWidgetTheme(document, readWidgetTheme(WIDGET_ID, { primary: "#d6d6d6", secondary: "#2e2e2e", scale: SCALE }, renderer));
  const core = resolveSetting({ root, widgetId: WIDGET_ID, options, renderer, key: "core", defaultValue: -1, validate: integerSetting({ minimum: -1 }) });
  const format = resolveSetting({ root, widgetId: WIDGET_ID, options, renderer, key: "format", defaultValue: core < 0 ? "{value} %" : "{core}: {value} %", validate: tokenFormatSetting(["value", "core", "zeroed-core"]) });
  const digitLimit = resolveSetting({ root, widgetId: WIDGET_ID, options, renderer, key: "digitLimit", defaultValue: 5, validate: integerSetting({ minimum: 1 }) });
  const label = resolveSetting({ root, widgetId: WIDGET_ID, options, renderer, key: "label", defaultValue: "CPU", validate: stringSetting() });
  const { updateInterval = 5 } = metricSettings({ root, widgetId: WIDGET_ID, options, renderer, keys: ["updateInterval"], defaults: { updateInterval: 5 } });
  const valueNode = document.querySelector('[data-webskin-frame-part="value"]');
  const subtextNode = document.querySelector('[data-webskin-frame-part="subtext"]');
  subtextNode.textContent = label;
  const render = (metrics, phase) => {
    const utilization = phase === "preview" ? { overall: 18.4, per_core: [] } : metrics?.cpu?.utilization;
    const raw = core < 0 ? utilization?.overall : utilization?.per_core?.[core];
    valueNode.textContent = formatMetricTemplate(format, {
      value: formatMetricNumber(raw, 1, digitLimit),
      core: core < 0 ? "-1" : String(core + 1),
      "zeroed-core": String(core),
    }, !Number.isFinite(Number(raw)));
  };
  const fields = core < 0 ? ["cpu.utilization.overall"] : ["cpu.utilization.per_core"];
  const close = startNyxMetricWidget({ root, window, renderer, widgetId: WIDGET_ID, fields, interval: updateInterval, render, preview: null, onError: () => { valueNode.textContent = "--"; } });
  onPageCleanup(root, () => close());
  return { destroy: () => destroyWidget(root) };
}

if (globalThis.__KHAOSSHELL_WIDGET_AUTOSTART__) mount(globalThis.document);
