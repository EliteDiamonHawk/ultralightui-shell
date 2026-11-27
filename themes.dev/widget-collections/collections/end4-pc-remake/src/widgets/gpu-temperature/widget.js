import { destroyWidget, onPageCleanup } from "@khaosshell/widget-runtime/lifecycle.js";
import { applyWidgetTheme, readWidgetTheme } from "@khaosshell/widget-runtime/theme.js";
import { formatMetricNumber, formatMetricTemplate, metricSettings, startNyxMetricWidget } from "@khaosshell/widget-runtime/metrics.js";
import { integerSetting, resolveSetting, stringSetting, tokenFormatSetting, widgetContext } from "@khaosshell/widget-runtime/settings.js";

const isGpu = (record) => /gpu|video|graphics/i.test(`${record?.hardware_type ?? ""} ${record?.hardware_name ?? ""}`);
const normalizedName = (record) => String(record?.name ?? "").toLowerCase().replace(/[\s_-]+/g, "");

const SCALE = 1;

export function mount(root = globalThis.document, options = {}) {
  if (!root) throw new Error("KhaosShell widget mount requires a root element or document.");
  const { document, window, renderer } = widgetContext(root, options);
  const WIDGET_ID = "gpu-temperature";
  applyWidgetTheme(document, readWidgetTheme(WIDGET_ID, { primary: "#d6d6d6", secondary: "#2e2e2e", scale: SCALE }, renderer));
  const sensorId = resolveSetting({ root, widgetId: WIDGET_ID, options, renderer, key: "sensorId", defaultValue: undefined, validate: stringSetting() });
  const gpuId = resolveSetting({ root, widgetId: WIDGET_ID, options, renderer, key: "gpuId", defaultValue: undefined, validate: stringSetting() });
  const format = resolveSetting({ root, widgetId: WIDGET_ID, options, renderer, key: "format", defaultValue: "{core-temperature} °C", validate: tokenFormatSetting(["core-temperature", "core-temp", "hot-spot-temperature", "hot-spot", "warning-temperature", "warning", "critical-temperature", "critical", "fan-speed", "fan", "gpu"]) });
  const digitLimit = resolveSetting({ root, widgetId: WIDGET_ID, options, renderer, key: "digitLimit", defaultValue: 5, validate: integerSetting({ minimum: 1 }) });
  const label = resolveSetting({ root, widgetId: WIDGET_ID, options, renderer, key: "label", defaultValue: "GPU", validate: stringSetting() });
  const { updateInterval = 5 } = metricSettings({ root, widgetId: WIDGET_ID, options, renderer, keys: ["updateInterval"], defaults: { updateInterval: 5 } });
  const valueNode = document.querySelector('[data-webskin-frame-part="value"]');
  const subtextNode = document.querySelector('[data-webskin-frame-part="subtext"]');
  subtextNode.textContent = label;
  const render = (metrics, phase) => {
    const readings = phase === "preview" ? [{ id: "preview-gpu-temp", hardware_id: "gpu0", hardware_name: "GPU", hardware_type: "gpu", name: "Core Temperature", type: "temperature", value: 54 }] : (metrics?.sensors?.readings ?? []);
    let records = readings;
    if (sensorId) {
      const selected = readings.find((record) => record?.id === sensorId);
      records = selected ? readings.filter((record) => record?.hardware_id === selected.hardware_id) : [];
    } else if (gpuId) {
      records = readings.filter((record) => record?.hardware_id === gpuId || record?.hardware_name === gpuId);
    } else {
      const selected = readings.find(isGpu);
      records = selected ? readings.filter((record) => record?.hardware_id === selected.hardware_id) : readings;
    }
    const core = records.find((record) => record?.type === "temperature" && !normalizedName(record).includes("hotspot") && !normalizedName(record).includes("junction"));
    const hotSpot = records.find((record) => record?.type === "temperature" && (normalizedName(record).includes("hotspot") || normalizedName(record).includes("junction")));
    const fan = records.find((record) => record?.type === "fan");
    const warning = records.find((record) => record?.warning !== undefined);
    const critical = records.find((record) => record?.critical !== undefined) ?? core;
    const temperature = (record) => formatMetricNumber(record?.value, 1, digitLimit);
    valueNode.textContent = formatMetricTemplate(format, {
      "core-temperature": temperature(core),
      "core-temp": temperature(core),
      "hot-spot-temperature": temperature(hotSpot),
      "hot-spot": temperature(hotSpot),
      "warning-temperature": formatMetricNumber(warning?.warning ?? warning?.value, 1, digitLimit),
      warning: formatMetricNumber(warning?.warning ?? warning?.value, 1, digitLimit),
      "critical-temperature": formatMetricNumber(critical?.critical ?? critical?.value, 1, digitLimit),
      critical: formatMetricNumber(critical?.critical ?? critical?.value, 1, digitLimit),
      "fan-speed": formatMetricNumber(fan?.value, 0, digitLimit),
      fan: formatMetricNumber(fan?.value, 0, digitLimit),
      gpu: records[0]?.hardware_name ?? "--",
    }, !Number.isFinite(Number(core?.value)));
  };
  const close = startNyxMetricWidget({ root, window, renderer, widgetId: WIDGET_ID, fields: ["sensors.readings"], interval: updateInterval, render, preview: null, onError: () => { valueNode.textContent = "--"; } });
  onPageCleanup(root, () => close());
  return { destroy: () => destroyWidget(root) };
}

if (globalThis.__KHAOSSHELL_WIDGET_AUTOSTART__) mount(globalThis.document);
