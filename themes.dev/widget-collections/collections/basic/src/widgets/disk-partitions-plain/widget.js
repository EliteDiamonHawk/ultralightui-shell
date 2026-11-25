import { hasAetherBridge, openNyxMetricsStream, readNyxMetrics } from "@khaosshell/widget-runtime/bridge.js";
import { formatPercent } from "@khaosshell/widget-runtime/format.js";
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

const WIDGET_ID = "disk-partitions-plain";
const SCALE = 1;
document.documentElement.style.setProperty("--widget-scale", String(SCALE));

const FIELDS = ["disks.partitions", "disks.usage"];
const valueNode = document.querySelector('[data-basic-part="value"]');
const detailNode = document.querySelector('[data-basic-part="detail"]');
let closeStream = async () => {};

const render = (metrics, source) => {
  const disks = source === "preview"
    ? { partitions: [{ mount_point: "/", filesystem: "ntfs" }], usage: [{ mount_point: "/", percent: 61.2 }] }
    : metrics?.disks;
  const partition = Array.isArray(disks?.partitions) ? disks.partitions[0] : null;
  const usage = Array.isArray(disks?.usage) ? disks.usage.find((item) => item?.mount_point === partition?.mount_point) : null;
  const fallback = source === "preview" ? "-" : "n/a";
  valueNode.textContent = partition?.mount_point ?? fallback;
  detailNode.textContent = `${partition?.filesystem ?? fallback}\nusage ${Number.isFinite(Number(usage?.percent)) ? formatPercent(usage.percent) : fallback}`;
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
