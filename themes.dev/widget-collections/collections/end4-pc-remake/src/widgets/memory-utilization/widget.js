import { destroyWidget, onPageCleanup } from "@khaosshell/widget-runtime/lifecycle.js";
import { applyWidgetTheme, readWidgetTheme } from "@khaosshell/widget-runtime/theme.js";
import { formatMetricBytesParts, formatMetricNumber, formatMetricTemplate, metricSettings, startNyxMetricWidget } from "@khaosshell/widget-runtime/metrics.js";
import { integerSetting, resolveSetting, stringSetting, tokenFormatSetting, widgetContext } from "@khaosshell/widget-runtime/settings.js";

const SCALE = 1;

export function mount(root = globalThis.document, options = {}) {
  if (!root) throw new Error("KhaosShell widget mount requires a root element or document.");
  const { document, window, renderer } = widgetContext(root, options);
  const WIDGET_ID = "memory-utilization";
  applyWidgetTheme(document, readWidgetTheme(WIDGET_ID, { primary: "#d6d6d6", secondary: "#2e2e2e", scale: SCALE }, renderer));
  const format = resolveSetting({ root, widgetId: WIDGET_ID, options, renderer, key: "format", defaultValue: "{usage}%", validate: tokenFormatSetting(["total", "total-unit", "avail", "available", "avail-unit", "available-unit", "used", "used-unit", "free", "free-unit", "usage", "percent"]) });
  const digitLimit = resolveSetting({ root, widgetId: WIDGET_ID, options, renderer, key: "digitLimit", defaultValue: 5, validate: integerSetting({ minimum: 1 }) });
  const label = resolveSetting({ root, widgetId: WIDGET_ID, options, renderer, key: "label", defaultValue: "RAM", validate: stringSetting() });
  const { updateInterval = 5 } = metricSettings({ root, widgetId: WIDGET_ID, options, renderer, keys: ["updateInterval"], defaults: { updateInterval: 5 } });
  const valueNode = document.querySelector('[data-webskin-frame-part="value"]');
  const subtextNode = document.querySelector('[data-webskin-frame-part="subtext"]');
  subtextNode.textContent = label;
  const render = (metrics, phase) => {
    const memory = phase === "preview" ? { total_bytes: 16_106_127_360, available_bytes: 9_284_853_760, used_bytes: 6_821_273_600, free_bytes: 9_284_853_760, percent: 42.6 } : metrics?.memory?.virtual;
    const total = formatMetricBytesParts(memory?.total_bytes, digitLimit);
    const available = formatMetricBytesParts(memory?.available_bytes, digitLimit);
    const used = formatMetricBytesParts(memory?.used_bytes, digitLimit);
    const free = formatMetricBytesParts(memory?.free_bytes, digitLimit);
    valueNode.textContent = formatMetricTemplate(format, {
      total: total.value,
      "total-unit": total.unit,
      avail: available.value,
      available: available.value,
      "avail-unit": available.unit,
      "available-unit": available.unit,
      used: used.value,
      "used-unit": used.unit,
      free: free.value,
      "free-unit": free.unit,
      usage: formatMetricNumber(memory?.percent, 1, digitLimit),
      percent: formatMetricNumber(memory?.percent, 1, digitLimit),
    }, !Number.isFinite(Number(memory?.percent)));
  };
  const close = startNyxMetricWidget({ root, window, renderer, widgetId: WIDGET_ID, fields: ["memory.virtual"], interval: updateInterval, render, preview: null, onError: () => { valueNode.textContent = "--"; } });
  onPageCleanup(root, () => close());
  return { destroy: () => destroyWidget(root) };
}

if (globalThis.__KHAOSSHELL_WIDGET_AUTOSTART__) mount(globalThis.document);
