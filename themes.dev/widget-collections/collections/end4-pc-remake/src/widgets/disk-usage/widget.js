import { destroyWidget, onPageCleanup } from "@khaosshell/widget-runtime/lifecycle.js";
import { applyWidgetTheme, readWidgetTheme } from "@khaosshell/widget-runtime/theme.js";
import { formatMetricBytesParts, formatMetricNumber, formatMetricTemplate, isMetricValueAvailable, metricSettings, startNyxMetricWidget } from "@khaosshell/widget-runtime/metrics.js";
import { integerSetting, resolveSetting, stringSetting, tokenFormatSetting, widgetContext } from "@khaosshell/widget-runtime/settings.js";

const SCALE = 1;

export function mount(root = globalThis.document, options = {}) {
  if (!root) throw new Error("KhaosShell widget mount requires a root element or document.");
  const { document, window, renderer } = widgetContext(root, options);
  const WIDGET_ID = "disk-usage";
  applyWidgetTheme(document, readWidgetTheme(WIDGET_ID, { primary: "#d6d6d6", secondary: "#2e2e2e", scale: SCALE }, renderer));
  const mountPoint = resolveSetting({ root, widgetId: WIDGET_ID, options, renderer, key: "mountPoint", defaultValue: undefined, validate: stringSetting() });
  const format = resolveSetting({ root, widgetId: WIDGET_ID, options, renderer, key: "format", defaultValue: "{usage}%", validate: tokenFormatSetting(["mount-point", "filesystem", "total", "total-unit", "used", "used-unit", "free", "free-unit", "usage", "percent"]) });
  const digitLimit = resolveSetting({ root, widgetId: WIDGET_ID, options, renderer, key: "digitLimit", defaultValue: 5, validate: integerSetting({ minimum: 1 }) });
  const label = resolveSetting({ root, widgetId: WIDGET_ID, options, renderer, key: "label", defaultValue: "Disk", validate: stringSetting() });
  const { updateInterval = 60 } = metricSettings({ root, widgetId: WIDGET_ID, options, renderer, keys: ["updateInterval"], defaults: { updateInterval: 60 } });
  const valueNode = document.querySelector('[data-webskin-frame-part="value"]');
  const subtextNode = document.querySelector('[data-webskin-frame-part="subtext"]');
  subtextNode.textContent = label;
  const render = (metrics, phase) => {
    const usage = phase === "preview" ? { mount_point: "C:", percent: 46, total_bytes: 100, used_bytes: 46, free_bytes: 54 } : (metrics?.disks?.usage ?? []).find((item) => !mountPoint || item?.mount_point === mountPoint);
    const partition = phase === "preview" ? { filesystem: "NTFS" } : (metrics?.disks?.partitions ?? []).find((item) => item?.mount_point === usage?.mount_point);
    const total = formatMetricBytesParts(usage?.total_bytes, digitLimit);
    const used = formatMetricBytesParts(usage?.used_bytes, digitLimit);
    const free = formatMetricBytesParts(usage?.free_bytes, digitLimit);
    valueNode.textContent = formatMetricTemplate(format, {
      "mount-point": usage?.mount_point ?? "--",
      filesystem: partition?.filesystem ?? "--",
      total: total.value,
      "total-unit": total.unit,
      used: used.value,
      "used-unit": used.unit,
      free: free.value,
      "free-unit": free.unit,
      usage: formatMetricNumber(usage?.percent, 1, digitLimit),
      percent: formatMetricNumber(usage?.percent, 1, digitLimit),
    }, !isMetricValueAvailable(usage?.percent));
  };
  const close = startNyxMetricWidget({ root, window, renderer, widgetId: WIDGET_ID, fields: ["disks.usage", "disks.partitions"], interval: updateInterval, render, preview: null, onError: () => { valueNode.textContent = "--"; } });
  onPageCleanup(root, () => close());
  return { destroy: () => destroyWidget(root) };
}

if (globalThis.__KHAOSSHELL_WIDGET_AUTOSTART__) mount(globalThis.document);
