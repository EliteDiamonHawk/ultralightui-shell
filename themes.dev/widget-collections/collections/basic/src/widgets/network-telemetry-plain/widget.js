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

const WIDGET_ID = "network-telemetry-plain";
const SCALE = 1;
document.documentElement.style.setProperty("--widget-scale", String(SCALE));

const FIELD = "network";
const valueNode = document.querySelector('[data-basic-part="value"]');
const detailNode = document.querySelector('[data-basic-part="detail"]');
let closeStream = async () => {};

const render = (metrics, source) => {
  const network = source === "preview" ? { bytes_sent: 18_744_320, bytes_received: 82_581_504, packets_dropped: 0, transmission_errors: 0 } : metrics?.network;
  const fallback = source === "preview" ? "-" : "n/a";
  valueNode.textContent = network ? `sent ${formatBytes(network.bytes_sent)}` : fallback;
  detailNode.textContent = network
    ? `received ${formatBytes(network.bytes_received)}\ndropped ${Number.isFinite(Number(network.packets_dropped)) ? network.packets_dropped : fallback}\nerrors ${Number.isFinite(Number(network.transmission_errors)) ? network.transmission_errors : fallback}`
    : "";
};

const start = async () => {
  if (!hasAetherBridge()) {
    render(null, "preview");
    return;
  }
  try {
    render(await readNyxMetrics([FIELD]), "nyx");
    closeStream = await openNyxMetricsStream([FIELD], {
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
