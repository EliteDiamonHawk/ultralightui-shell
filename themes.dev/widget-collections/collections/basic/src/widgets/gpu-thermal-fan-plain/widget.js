import { hasAetherBridge, openNyxMetricsStream, readNyxMetrics } from "@khaosshell/widget-runtime/bridge.js";
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

const WIDGET_ID = "gpu-thermal-fan-plain";
const SCALE = 1;
document.documentElement.style.setProperty("--widget-scale", String(SCALE));

const FIELD = "sensors.readings";
const valueNode = document.querySelector('[data-basic-part="value"]');
const detailNode = document.querySelector('[data-basic-part="detail"]');
let closeStream = async () => {};

const render = (metrics, source) => {
  const readings = source === "preview" ? [{ type: "temperature", value: 48 }, { type: "fan", value: 1200 }] : metrics?.sensors?.readings;
  const temperature = Array.isArray(readings) ? readings.find((item) => item?.type === "temperature") : null;
  const fan = Array.isArray(readings) ? readings.find((item) => item?.type === "fan") : null;
  const fallback = source === "preview" ? "-" : "n/a";
  valueNode.textContent = temperature?.value === undefined ? fallback : `${temperature.value} °C`;
  detailNode.textContent = fan?.value === undefined ? "fan n/a" : `fan ${fan.value} RPM`;
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
