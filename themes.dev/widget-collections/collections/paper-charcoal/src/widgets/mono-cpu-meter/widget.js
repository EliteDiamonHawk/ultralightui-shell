import {
  hasAetherBridge,
  openNyxMetricsStream,
  previewMetrics,
  readNyxMetrics,
} from "@khaosshell/widget-runtime/bridge.js";
import { clamp, formatPercent } from "@khaosshell/widget-runtime/format.js";
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

const WIDGET_ID = "mono-cpu-meter";
const SCALE = 1;
const UPDATE_INTERVAL_SECONDS = 5;
const METRIC_FIELDS = ["cpu.utilization.overall"];

document.documentElement.style.setProperty("--widget-scale", String(SCALE));

const valueNode = document.querySelector("[data-value]");
const meterNode = document.querySelector("[data-meter]");
const detailNode = document.querySelector("[data-detail]");
let closeStream = async () => {};

const render = (metrics, source) => {
  const value = Number(metrics?.cpu?.utilization?.overall);
  if (!Number.isFinite(value)) {
    valueNode.textContent = "n/a";
    meterNode.style.width = "0%";
    detailNode.textContent = source === "preview" ? "preview data unavailable" : "Nyx data unavailable";
    return;
  }
  valueNode.textContent = formatPercent(value);
  meterNode.style.width = clamp(value, 0, 100) + "%";
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
