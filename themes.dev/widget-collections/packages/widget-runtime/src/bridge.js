const BACKEND_ID = "nyx";
const METRICS_DATA_ID = "nyx.system.metrics";
const METRICS_STREAM_TYPE = "json";
const MAX_PENDING_STREAM_EVENTS = 32;

const renderer = (explicitRenderer) => explicitRenderer ?? globalThis.window?.renderer;

const requireRenderer = (explicitRenderer) => {
  const current = renderer(explicitRenderer);
  if (!current?.request) throw new Error("Aether renderer bridge is unavailable.");
  return current;
};

const assertResponse = (response) => {
  if (!response || response.status >= 300 || response.error) {
    throw new Error(response?.error?.message ?? "Nyx request failed.");
  }
  return response.body;
};

export const hasAetherBridge = (explicitRenderer) => Boolean(renderer(explicitRenderer)?.request);

export const getAetherVariable = (key, fallback, explicitRenderer) => {
  const value = renderer(explicitRenderer)?.aether?.variables?.get?.(key);
  return value === undefined ? fallback : value;
};

export async function requestNyx(path, { method = "GET", body = null, renderer: explicitRenderer } = {}) {
  const response = await requireRenderer(explicitRenderer).request({
    backendId: BACKEND_ID,
    method,
    path,
    body,
  });
  return assertResponse(response);
}

export async function readNyxTime(explicitRenderer) {
  const body = await requestNyx("/v1/nyx/system/time", { renderer: explicitRenderer });
  return Number(body?.epochMilliseconds);
}

export async function readNyxMetrics(fields, explicitRenderer) {
  return requestNyx("/v1/nyx/system/metrics", {
    method: "GET",
    body: { fields },
    renderer: explicitRenderer,
  });
}

export async function callNyxControl(name, explicitRenderer) {
  return requestNyx("/v1/nyx/system/controls/" + encodeURIComponent(name), {
    method: "POST",
    renderer: explicitRenderer,
  });
}

export async function openNyxMetricsStream(fields, { interval = 5, onSnapshot, onError, renderer: explicitRenderer } = {}) {
  const current = requireRenderer(explicitRenderer);
  if (!current.openStream || !current.closeStream || !current.aether?.onEvent) {
    throw new Error("Aether stream bridge is unavailable.");
  }

  const encodedFields = encodeURIComponent(JSON.stringify(fields));
  const dataId = METRICS_DATA_ID + "?fields=" + encodedFields + "&interval=" + encodeURIComponent(interval);
  let subscription;
  let pendingEvents = [];
  const dispatch = (event) => {
    try {
      const message = JSON.parse(event.data?.json ?? "{}");
      if (message.type === "snapshot") onSnapshot?.(message.data);
      else if (message.type === "error") onError?.(new Error(message.message ?? message.code ?? "Nyx stream error."));
    } catch (error) {
      onError?.(error);
    }
  };
  const stop = current.aether.onEvent((event) => {
    if (
      event?.source !== "stream"
      || event.backendId !== BACKEND_ID
      || !event.streamId
    ) return;

    if (!subscription) {
      if (pendingEvents.length >= MAX_PENDING_STREAM_EVENTS) pendingEvents.shift();
      pendingEvents.push(event);
      return;
    }

    if (event.streamId === subscription.streamId) dispatch(event);
  });

  try {
    subscription = await current.openStream({
      backendId: BACKEND_ID,
      dataId,
      type: METRICS_STREAM_TYPE,
    });
    const earlyEvents = pendingEvents;
    pendingEvents = [];
    earlyEvents.forEach((event) => {
      if (event.streamId === subscription.streamId) dispatch(event);
    });
  } catch (error) {
    pendingEvents = [];
    stop?.();
    throw error;
  }

  return async () => {
    pendingEvents = [];
    stop?.();
    if (subscription?.streamId) {
      await current.closeStream({ backendId: BACKEND_ID, streamId: subscription.streamId });
    }
  };
}

export const previewMetrics = Object.freeze({
  memory: { virtual: { percent: 42.6, used_bytes: 6_821_273_600, total_bytes: 16_106_127_360 } },
  cpu: { utilization: { overall: 18.4 } },
  sensors: {
    battery: { percent: 78, seconds_left: 12_600, power_plugged: false },
  },
  network: { bytes_sent: 18_744_320, bytes_received: 82_581_504 },
});
