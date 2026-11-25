import { destroyWidget, onPageCleanup } from "@khaosshell/widget-runtime/lifecycle.js";
import { hasAetherBridge, openNyxMetricsStream, readNyxMetrics } from "@khaosshell/widget-runtime/bridge.js";
import { formatBytes, formatDuration, formatPercent } from "@khaosshell/widget-runtime/format.js";

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

const WIDGET_ID = "cpu-frequency";
const SCALE = 1;

document.documentElement.style.setProperty("--widget-scale", String(SCALE));


const FIELD = "cpu.frequencies.current_mhz";
const PREVIEW_VALUE = 3400;
const valueNode = document.querySelector("[data-webskin-frame-part=\"value\"]");
let closeStream = async () => {};

const readPath = (value, path) => path.split(".").reduce((current, key) => current?.[key], value);
const formatValue = (value) => {
  
  
  
  
  
  return Math.round(value);
  return String(Math.round(Number(value)));
};
const render = (metrics, source) => {
  const raw = source === "preview" ? PREVIEW_VALUE : readPath(metrics, FIELD);
  const normalized = raw;
  const value = Number(normalized);
  if (!Number.isFinite(value)) {
    valueNode.textContent = "--";
    return;
  }
  valueNode.textContent = formatValue(value);
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
      onError: () => { valueNode.textContent = "--"; },
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
