import {
  hasAetherBridge,
  openNyxMetricsStream,
  previewMetrics,
  readNyxMetrics,
} from "@khaosshell/widget-runtime/bridge.js";
import { formatBytes } from "@khaosshell/widget-runtime/format.js";
import { destroyWidget, onPageCleanup } from "@khaosshell/widget-runtime/lifecycle.js";

export function mount(root = globalThis.document, options = {}) {
  if (!root) throw new Error("KhaosShell widget mount requires a root element or document.");
  const pageDocument = root.ownerDocument ?? root;
  const queryRoot = root.querySelector ? root : pageDocument;
  const document = new Proxy(pageDocument, {
    get(target, property, receiver) {
      if (property === "querySelector") return queryRoot.querySelector.bind(queryRoot);
      if (property === "querySelectorAll") return queryRoot.querySelectorAll.bind(queryRoot);
      const value = Reflect.get(target, property, receiver);
      return typeof value === "function" ? value.bind(target) : value;
    },
  });
  const window = options.window ?? pageDocument.defaultView ?? globalThis.window;

const WIDGET_ID = "network-transfer";
const SCALE = 1;
const UPDATE_INTERVAL_SECONDS = 1;
const METRIC_FIELDS = ["network.bytes_sent", "network.bytes_received"];

document.documentElement.style.setProperty("--widget-scale", String(SCALE));

const uploadNode = document.querySelector("[data-upload]");
const downloadNode = document.querySelector("[data-download]");
const detailNode = document.querySelector("[data-detail]");
let previous;
let previousAt;
let closeStream = async () => {};

const render = (metrics, source) => {
  const network = metrics?.network;
  const sent = Number(network?.bytes_sent);
  const received = Number(network?.bytes_received);
  const now = performance.now();
  if (!Number.isFinite(sent) || !Number.isFinite(received)) {
    uploadNode.textContent = "n/a";
    downloadNode.textContent = "n/a";
    detailNode.textContent = "network data unavailable";
    return;
  }

  let uploadRate = 0;
  let downloadRate = 0;
  if (previous && previousAt !== undefined) {
    const seconds = Math.max(.001, (now - previousAt) / 1000);
    uploadRate = Math.max(0, sent - previous.sent) / seconds;
    downloadRate = Math.max(0, received - previous.received) / seconds;
  }
  previous = { sent, received };
  previousAt = now;
  uploadNode.textContent = formatBytes(uploadRate) + "/s";
  downloadNode.textContent = formatBytes(downloadRate) + "/s";
  detailNode.textContent = source === "preview" ? "preview sample" : "live from Nyx";
};

const start = async () => {
  if (!hasAetherBridge()) {
    render(previewMetrics, "preview");
    return;
  }
  try {
    render(await readNyxMetrics(METRIC_FIELDS), "nyx");
    closeStream = await openNyxMetricsStream(METRIC_FIELDS, {
      interval: UPDATE_INTERVAL_SECONDS,
      onSnapshot: (metrics) => render(metrics, "nyx"),
      onError: () => detailNode.textContent = "Nyx stream unavailable",
    });
  } catch {
    render(null, "nyx");
  }
};

void start();
onPageCleanup(root, () => closeStream());

void WIDGET_ID;

  return { destroy: () => destroyWidget(root) };
}

if (globalThis.__KHAOSSHELL_WIDGET_AUTOSTART__) mount(globalThis.document);
