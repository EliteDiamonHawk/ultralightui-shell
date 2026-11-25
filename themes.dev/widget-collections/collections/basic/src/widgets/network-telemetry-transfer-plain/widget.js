import { hasAetherBridge, openNyxMetricsStream, readNyxMetrics } from "@khaosshell/widget-runtime/bridge.js";
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

const WIDGET_ID = "network-telemetry-transfer-plain";
const SCALE = 1;
document.documentElement.style.setProperty("--widget-scale", String(SCALE));

const FIELDS = ["network.bytes_sent", "network.bytes_received"];
const valueNode = document.querySelector('[data-basic-part="value"]');
const detailNode = document.querySelector('[data-basic-part="detail"]');
const preview = { bytes_sent: 18_744_320, bytes_received: 82_581_504 };
const transferState = {};
let closeStream = async () => {};

const render = (metrics, source) => {
  const network = source === "preview" ? preview : metrics?.network;
  const received = Number(network?.bytes_received);
  const sent = Number(network?.bytes_sent);
  const now = performance.now();
  let value = source === "preview" ? "-" : "n/a";
  let detail = "";
  if (transferState.at && now > transferState.at && Number.isFinite(received) && Number.isFinite(sent)) {
    const seconds = (now - transferState.at) / 1000;
    value = `${formatBytes(Math.max(0, received - transferState.received) / seconds)}/s`;
    detail = `sent ${formatBytes(Math.max(0, sent - transferState.sent) / seconds)}/s`;
  }
  if (Number.isFinite(received) && Number.isFinite(sent)) {
    transferState.at = now;
    transferState.received = received;
    transferState.sent = sent;
  }
  valueNode.textContent = value;
  detailNode.textContent = detail;
};

const start = async () => {
  if (!hasAetherBridge()) {
    render(null, "preview");
    return;
  }
  try {
    render(await readNyxMetrics(FIELDS), "nyx");
    closeStream = await openNyxMetricsStream(FIELDS, {
      interval: 5,
      onSnapshot: (metrics) => render(metrics, "nyx"),
      onError: () => render(null, "nyx"),
    });
  } catch {
    render(null, "nyx");
  }
};

render(null, "initial");
void start();
onPageCleanup(root, () => closeStream());

void WIDGET_ID;

  return { destroy: () => destroyWidget(root) };
}

if (globalThis.__KHAOSSHELL_WIDGET_AUTOSTART__) mount(globalThis.document);
