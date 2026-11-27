import { destroyWidget, onPageCleanup } from "@khaosshell/widget-runtime/lifecycle.js";
import { applyWidgetTheme, readWidgetTheme } from "@khaosshell/widget-runtime/theme.js";
import { deriveNetworkTransfer, formatMetricBytesParts, formatMetricNumber, formatMetricTemplate, metricSettings, startNyxMetricWidget } from "@khaosshell/widget-runtime/metrics.js";
import { integerSetting, resolveSetting, stringSetting, tokenFormatSetting, widgetContext } from "@khaosshell/widget-runtime/settings.js";

const SCALE = 1;

export function mount(root = globalThis.document, options = {}) {
  if (!root) throw new Error("KhaosShell widget mount requires a root element or document.");
  const { document, window, renderer } = widgetContext(root, options);
  const WIDGET_ID = "network-received";
  applyWidgetTheme(document, readWidgetTheme(WIDGET_ID, { primary: "#d6d6d6", secondary: "#2e2e2e", scale: SCALE }, renderer));
  const format = resolveSetting({ root, widgetId: WIDGET_ID, options, renderer, key: "format", defaultValue: "{download-speed} {download-speed-unit}/s", validate: tokenFormatSetting(["uploaded", "sent", "uploaded-unit", "sent-unit", "downloaded", "received", "downloaded-unit", "received-unit", "upload-speed", "upload-speed-unit", "upload-rate", "download-speed", "download-speed-unit", "download-rate", "utilization"]) });
  const digitLimit = resolveSetting({ root, widgetId: WIDGET_ID, options, renderer, key: "digitLimit", defaultValue: 5, validate: integerSetting({ minimum: 1 }) });
  const label = resolveSetting({ root, widgetId: WIDGET_ID, options, renderer, key: "label", defaultValue: "Download", validate: stringSetting() });
  const { updateInterval = 5 } = metricSettings({ root, widgetId: WIDGET_ID, options, renderer, keys: ["updateInterval"], defaults: { updateInterval: 5 } });
  const valueNode = document.querySelector('[data-webskin-frame-part="value"]');
  const subtextNode = document.querySelector('[data-webskin-frame-part="subtext"]');
  subtextNode.textContent = label;
  let previous;
  const mapSnapshot = (metrics) => {
    const derived = deriveNetworkTransfer(metrics, previous);
    previous = derived.previous;
    return derived.metrics;
  };
  const render = (metrics, phase) => {
    const transfer = phase === "preview" ? { uploaded: 18_744_320, downloaded: 82_581_504, upload_speed: 512, download_speed: 2048, utilization: 0 } : metrics?.network?.transfer;
    const uploaded = formatMetricBytesParts(transfer?.uploaded, digitLimit);
    const downloaded = formatMetricBytesParts(transfer?.downloaded, digitLimit);
    const uploadSpeed = formatMetricBytesParts(transfer?.upload_speed, digitLimit);
    const downloadSpeed = formatMetricBytesParts(transfer?.download_speed, digitLimit);
    const rate = (parts) => parts.value === "--" ? "--" : `${parts.value} ${parts.unit}/s`;
    valueNode.textContent = formatMetricTemplate(format, {
      uploaded: uploaded.value,
      sent: uploaded.value,
      "uploaded-unit": uploaded.unit,
      "sent-unit": uploaded.unit,
      downloaded: downloaded.value,
      received: downloaded.value,
      "downloaded-unit": downloaded.unit,
      "received-unit": downloaded.unit,
      "upload-speed": uploadSpeed.value,
      "upload-speed-unit": uploadSpeed.unit,
      "upload-rate": rate(uploadSpeed),
      "download-speed": downloadSpeed.value,
      "download-speed-unit": downloadSpeed.unit,
      "download-rate": rate(downloadSpeed),
      utilization: formatMetricNumber(transfer?.utilization, 1, digitLimit),
    }, downloadSpeed.value === "--");
  };
  const close = startNyxMetricWidget({ root, window, renderer, widgetId: WIDGET_ID, fields: ["network.bytes_sent", "network.bytes_received"], interval: updateInterval, render, preview: null, mapSnapshot, onError: () => { valueNode.textContent = "--"; } });
  onPageCleanup(root, () => close());
  return { destroy: () => destroyWidget(root) };
}

if (globalThis.__KHAOSSHELL_WIDGET_AUTOSTART__) mount(globalThis.document);
