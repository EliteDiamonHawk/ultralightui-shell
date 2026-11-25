import { getAetherVariable } from "@khaosshell/widget-runtime/bridge.js";
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

const WIDGET_ID = "charcoal-countdown";
const SCALE = 1;
const DEFAULT_DURATION_MS = 5 * 60 * 1000;
const UPDATE_INTERVAL_MS = 100;
const DURATION_VARIABLE = "countdown-duration-ms";

document.documentElement.style.setProperty("--widget-scale", String(SCALE));

const valueNode = document.querySelector("[data-value]");
const statusNode = document.querySelector("[data-status]");
const startButton = document.querySelector("[data-start]");
const pauseButton = document.querySelector("[data-pause]");
const resetButton = document.querySelector("[data-reset]");
const configuredDuration = Number(getAetherVariable(DURATION_VARIABLE, DEFAULT_DURATION_MS));
const initialDuration = Number.isFinite(configuredDuration) && configuredDuration > 0 ? configuredDuration : DEFAULT_DURATION_MS;
let remaining = initialDuration;
let startedAt = 0;
let running = false;

const render = () => {
  const current = running ? Math.max(0, remaining - (performance.now() - startedAt)) : remaining;
  valueNode.textContent = formatDuration(current);
  statusNode.textContent = running ? "running" : current <= 0 ? "finished" : "ready";
  if (running && current <= 0) {
    remaining = 0;
    running = false;
  }
};

startButton.addEventListener("click", () => {
  if (!running && remaining > 0) {
    startedAt = performance.now();
    running = true;
    render();
  }
});

pauseButton.addEventListener("click", () => {
  if (running) {
    remaining = Math.max(0, remaining - (performance.now() - startedAt));
    running = false;
    render();
  }
});

resetButton.addEventListener("click", () => {
  remaining = initialDuration;
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
