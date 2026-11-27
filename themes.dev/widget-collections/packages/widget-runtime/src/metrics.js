import { hasAetherBridge, openNyxMetricsStream, readNyxMetrics } from "./bridge.js";
import { nyxIntervalSetting, resolveSetting } from "./settings.js";

export const readPath = (value, path) => path.split(".").reduce((current, key) => current?.[key], value);

export const isMetricValueAvailable = (value) => value !== undefined && value !== null && Number.isFinite(Number(value));

export const metricSettings = ({ root, widgetId, options, renderer, defaults = {}, keys = [] }) => Object.fromEntries(keys.map((key) => [
  key,
  resolveSetting({
    root,
    widgetId,
    options,
    renderer,
    key,
    defaultValue: defaults[key],
    validate: key === "updateInterval" ? nyxIntervalSetting : undefined,
  }),
]));

export const startNyxMetricWidget = ({ root, window, renderer, widgetId, fields, interval, render, preview, onError, mapSnapshot }) => {
  let closeStream;
  let disposed = false;
  const start = async () => {
    if (!hasAetherBridge(renderer)) {
      if (!disposed) render(preview, "preview");
      return;
    }
    try {
      const snapshot = await readNyxMetrics(fields, renderer);
      if (disposed) return;
      render(mapSnapshot ? mapSnapshot(snapshot) : snapshot, "nyx");
      const cleanup = await openNyxMetricsStream(fields, {
        interval,
        renderer,
        onSnapshot: (metrics) => {
          if (!disposed) render(mapSnapshot ? mapSnapshot(metrics) : metrics, "nyx");
        },
        onError: (error) => {
          if (!disposed) onError?.(error);
        },
      });
      if (disposed) await cleanup?.();
      else closeStream = cleanup;
    } catch {
      if (!disposed) render(null, "error");
    }
  };
  void start();
  return async () => {
    disposed = true;
    const cleanup = closeStream;
    closeStream = undefined;
    await cleanup?.();
  };
};

export const formatMetricNumber = (value, digits = 1, digitLimit) => {
  if (!isMetricValueAvailable(value)) return "--";
  const number = Number(value);
  const rendered = number.toFixed(digits);
  if (!digitLimit || rendered.replace(/\D/g, "").length <= digitLimit) return rendered;
  const sign = rendered.startsWith("-") ? "-" : "";
  const unsigned = rendered.slice(sign.length);
  const [integer, fraction = ""] = unsigned.split(".");
  const digitsOnly = (integer + fraction).slice(0, digitLimit);
  return integer.length >= digitLimit
    ? sign + digitsOnly
    : sign + digitsOnly.slice(0, integer.length) + (digitsOnly.slice(integer.length) ? "." + digitsOnly.slice(integer.length) : "");
};

export const formatMetricPercent = (value, digitLimit) => `${formatMetricNumber(value, 1, digitLimit)}%`;

export const formatMetricTemplate = (format, values, unavailable = false) => {
  if (unavailable) return "--";
  return format.replace(/\{([a-z][a-z0-9-]*)\}/g, (_, token) => values[token] ?? "--");
};

const trimMetricDigits = (value, digitLimit) => {
  if (!digitLimit || value === "--") return value;
  const sign = value.startsWith("-") ? "-" : "";
  const unsigned = value.slice(sign.length);
  const [integer, fraction = ""] = unsigned.split(".");
  if ((integer + fraction).length <= digitLimit) return value;
  if (integer.length >= digitLimit) return sign + integer.slice(0, digitLimit);
  const fractionLimit = Math.max(0, digitLimit - integer.length);
  return sign + integer + (fractionLimit > 0 ? `.${fraction.slice(0, fractionLimit)}` : "");
};

export const formatMetricBytesParts = (value, digitLimit) => {
  if (!isMetricValueAvailable(value)) return { value: "--", unit: "--" };
  let number = Number(value);
  const units = ["B", "KB", "MB", "GB", "TB"];
  let index = 0;
  while (number >= 1024 && index < units.length - 1) {
    number /= 1024;
    index += 1;
  }
  return { value: trimMetricDigits(number.toFixed(index > 1 ? 1 : 0), digitLimit), unit: units[index] };
};

export const formatMetricBytes = (value, digitLimit) => {
  const formatted = formatMetricBytesParts(value, digitLimit);
  return formatted.value === "--" ? "--" : `${formatted.value} ${formatted.unit}`;
};

export const deriveNetworkTransfer = (metrics, previous, now = Date.now()) => {
  const network = metrics?.network ?? {};
  if (network.available === false) {
    return {
      metrics: { ...metrics, network: { ...network, transfer: undefined } },
      previous: undefined,
    };
  }

  const received = network.bytes_received === undefined || network.bytes_received === null
    ? undefined
    : Number(network.bytes_received);
  const sent = network.bytes_sent === undefined || network.bytes_sent === null
    ? undefined
    : Number(network.bytes_sent);
  const elapsed = previous && Number.isFinite(previous.now) ? (now - previous.now) / 1000 : 0;
  const hasElapsed = elapsed > 0;
  const downloadSpeed = hasElapsed && Number.isFinite(received) && Number.isFinite(previous.received)
    ? Math.max(0, received - previous.received) / elapsed
    : undefined;
  const uploadSpeed = hasElapsed && Number.isFinite(sent) && Number.isFinite(previous.sent)
    ? Math.max(0, sent - previous.sent) / elapsed
    : undefined;
  const nextPrevious = Number.isFinite(received) || Number.isFinite(sent)
    ? { now, received, sent }
    : undefined;
  return {
    metrics: {
      ...metrics,
      network: {
        ...network,
        transfer: { uploaded: sent, downloaded: received, upload_speed: uploadSpeed, download_speed: downloadSpeed },
      },
    },
    previous: nextPrevious,
  };
};

