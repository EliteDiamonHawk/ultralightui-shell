import { hasAetherBridge, readNyxTime } from "@khaosshell/widget-runtime/bridge.js";
import { destroyWidget, onPageCleanup } from "@khaosshell/widget-runtime/lifecycle.js";
import { applyWidgetTheme, readWidgetTheme } from "@khaosshell/widget-runtime/theme.js";
import { formatMetricNumber, formatMetricTemplate, metricSettings, startNyxMetricWidget } from "@khaosshell/widget-runtime/metrics.js";
import { resolveSetting, stringSetting, tokenFormatSetting, widgetContext } from "@khaosshell/widget-runtime/settings.js";

const SCALE = 1;

export function mount(root = globalThis.document, options = {}) {
  if (!root) throw new Error("KhaosShell widget mount requires a root element or document.");
  const { document, window, renderer } = widgetContext(root, options);
  const WIDGET_ID = "last-boot-time";
  applyWidgetTheme(document, readWidgetTheme(WIDGET_ID, { primary: "#d6d6d6", secondary: "#2e2e2e", scale: SCALE }, renderer));
  const format = resolveSetting({ root, widgetId: WIDGET_ID, options, renderer, key: "format", defaultValue: "{uptime} s", validate: tokenFormatSetting(["boot-time", "uptime"]) });
  const digitLimit = resolveSetting({ root, widgetId: WIDGET_ID, options, renderer, key: "digitLimit", defaultValue: 5, validate: (value, key) => {
    if (!Number.isInteger(value) || value < 1) throw new RangeError(`${key} must be a positive integer`);
    return value;
  } });
  const label = resolveSetting({ root, widgetId: WIDGET_ID, options, renderer, key: "label", defaultValue: "Uptime", validate: stringSetting() });
  const { updateInterval = 60 } = metricSettings({ root, widgetId: WIDGET_ID, options, renderer, keys: ["updateInterval"], defaults: { updateInterval: 60 } });
  const valueNode = document.querySelector('[data-webskin-frame-part="value"]');
  const subtextNode = document.querySelector('[data-webskin-frame-part="subtext"]');
  subtextNode.textContent = label;
  let latestBootTime;
  let epochOffset = 0;
  const render = (metrics, phase) => {
    const bootTime = phase === "preview" ? new Date(Date.now() - 8120 * 1000).toISOString() : metrics?.system?.boot_time ?? latestBootTime;
    latestBootTime = bootTime;
    const uptime = bootTime ? Math.max(0, (Date.now() + epochOffset - Date.parse(bootTime)) / 1000) : undefined;
    valueNode.textContent = formatMetricTemplate(format, {
      "boot-time": bootTime ? new Date(bootTime).toLocaleString() : "--",
      uptime: formatMetricNumber(uptime, 0, digitLimit),
    }, !bootTime || !Number.isFinite(Number(uptime)));
  };
  let disposed = false;
  const syncTime = async () => {
    if (disposed || !hasAetherBridge(renderer)) return;
    try {
      const epoch = await readNyxTime(renderer);
      if (!disposed && Number.isFinite(epoch)) epochOffset = epoch - Date.now();
    } catch {
    }
  };
  const close = startNyxMetricWidget({ root, window, renderer, widgetId: WIDGET_ID, fields: ["system.boot_time"], interval: updateInterval, render, preview: null, onError: () => { valueNode.textContent = "--"; } });
  const tick = window?.setInterval?.(() => { if (latestBootTime) render({ system: { boot_time: latestBootTime } }, "nyx"); }, 1000);
  const syncTimer = window?.setInterval?.(syncTime, 60_000);
  void syncTime();
  onPageCleanup(root, async () => {
    disposed = true;
    if (tick !== undefined) window?.clearInterval?.(tick);
    if (syncTimer !== undefined) window?.clearInterval?.(syncTimer);
    await close();
  });

  return { destroy: () => destroyWidget(root) };
}

if (globalThis.__KHAOSSHELL_WIDGET_AUTOSTART__) mount(globalThis.document);
