import { destroyWidget, onPageCleanup } from "@khaosshell/widget-runtime/lifecycle.js";
import { applyWidgetTheme, readWidgetTheme } from "@khaosshell/widget-runtime/theme.js";
import { formatMetricBytesParts, formatMetricNumber, formatMetricTemplate, metricSettings, startNyxMetricWidget } from "@khaosshell/widget-runtime/metrics.js";
import { integerSetting, resolveSetting, stringSetting, tokenFormatSetting, widgetContext } from "@khaosshell/widget-runtime/settings.js";

const SCALE = 1;

export function mount(root = globalThis.document, options = {}) {
  if (!root) throw new Error("KhaosShell widget mount requires a root element or document.");
  const { document, window, renderer } = widgetContext(root, options);
  const WIDGET_ID = "swap-utilization";
  applyWidgetTheme(document, readWidgetTheme(WIDGET_ID, { primary: "#d6d6d6", secondary: "#2e2e2e", scale: SCALE }, renderer));
  const metric = resolveSetting({ root, widgetId: WIDGET_ID, options, renderer, key: "metric", defaultValue: "usage", validate: (value, key) => {
    if (!["total", "used", "free", "usage"].includes(value)) throw new RangeError(`${key} must be total, used, free, or usage`);
    return value;
  } });
  const format = resolveSetting({ root, widgetId: WIDGET_ID, options, renderer, key: "format", defaultValue: metric === "usage" ? "{value}%" : "{value} {unit}", validate: tokenFormatSetting(["value", "unit", "total", "total-unit", "used", "used-unit", "free", "free-unit", "usage", "percent"]) });
  const digitLimit = resolveSetting({ root, widgetId: WIDGET_ID, options, renderer, key: "digitLimit", defaultValue: 5, validate: integerSetting({ minimum: 1 }) });
  const label = resolveSetting({ root, widgetId: WIDGET_ID, options, renderer, key: "label", defaultValue: "Swap", validate: stringSetting() });
  const { updateInterval = 5 } = metricSettings({ root, widgetId: WIDGET_ID, options, renderer, keys: ["updateInterval"], defaults: { updateInterval: 5 } });
  const valueNode = document.querySelector('[data-webskin-frame-part="value"]');
  const subtextNode = document.querySelector('[data-webskin-frame-part="subtext"]');
  subtextNode.textContent = label;
  const render = (metrics, phase) => {
    const swap = phase === "preview" ? { total_bytes: 4_294_967_296, used_bytes: 515_396_075, free_bytes: 3_779_571_221, percent: 12 } : metrics?.memory?.swap;
    const total = formatMetricBytesParts(swap?.total_bytes, digitLimit);
    const used = formatMetricBytesParts(swap?.used_bytes, digitLimit);
    const free = formatMetricBytesParts(swap?.free_bytes, digitLimit);
    const selected = metric === "usage" ? { value: formatMetricNumber(swap?.percent, 1, digitLimit), unit: "%" } : ({ total, used, free })[metric];
    valueNode.textContent = formatMetricTemplate(format, {
      value: selected?.value ?? "--",
      unit: selected?.unit ?? "--",
      total: total.value,
      "total-unit": total.unit,
      used: used.value,
      "used-unit": used.unit,
      free: free.value,
      "free-unit": free.unit,
      usage: formatMetricNumber(swap?.percent, 1, digitLimit),
      percent: formatMetricNumber(swap?.percent, 1, digitLimit),
    }, selected?.value === "--");
  };
  const close = startNyxMetricWidget({ root, window, renderer, widgetId: WIDGET_ID, fields: ["memory.swap"], interval: updateInterval, render, preview: null, onError: () => { valueNode.textContent = "--"; } });
  onPageCleanup(root, () => close());
  return { destroy: () => destroyWidget(root) };
}

if (globalThis.__KHAOSSHELL_WIDGET_AUTOSTART__) mount(globalThis.document);
