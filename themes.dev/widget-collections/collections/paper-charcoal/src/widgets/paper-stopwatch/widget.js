import { formatDuration } from "@khaosshell/widget-runtime/format.js";
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

const WIDGET_ID = "paper-stopwatch";
const SCALE = 1;
const UPDATE_INTERVAL_MS = 100;

document.documentElement.style.setProperty("--widget-scale", String(SCALE));

const valueNode = document.querySelector("[data-value]");
const statusNode = document.querySelector("[data-status]");
const startButton = document.querySelector("[data-start]");
const pauseButton = document.querySelector("[data-pause]");
const resetButton = document.querySelector("[data-reset]");
let elapsed = 0;
let startedAt = 0;
let running = false;

const render = () => {
  const current = running ? elapsed + performance.now() - startedAt : elapsed;
  valueNode.textContent = formatDuration(current);
  statusNode.textContent = running ? "running" : "ready";
};

startButton.addEventListener("click", () => {
  if (!running) {
    startedAt = performance.now();
    running = true;
    render();
  }
});

pauseButton.addEventListener("click", () => {
  if (running) {
    elapsed += performance.now() - startedAt;
    running = false;
    render();
  }
});

resetButton.addEventListener("click", () => {
  elapsed = 0;
  running = false;
  render();
});

render();
const timer = window.setInterval(render, UPDATE_INTERVAL_MS);
onPageCleanup(root, () => window.clearInterval(timer));

void WIDGET_ID;

  return { destroy: () => destroyWidget(root) };
}

if (globalThis.__KHAOSSHELL_WIDGET_AUTOSTART__) mount(globalThis.document);
