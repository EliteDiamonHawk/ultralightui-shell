import { destroyWidget, onPageCleanup } from "@khaosshell/widget-runtime/lifecycle.js";
import { formatDuration } from "@khaosshell/widget-runtime/format.js";

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

const WIDGET_ID = "softburst-countdown";
const SCALE = 1;

document.documentElement.style.setProperty("--widget-scale", String(SCALE));


const valueNode = document.querySelector("[data-webskin-frame-part=\"value\"]");
const firstButton = document.querySelector("[data-timer-first]");
const secondButton = document.querySelector("[data-timer-second]");
let elapsed = 0;
let startedAt = 0;
let running = false;
let remaining = 0;

const render = () => {
  const current = running ? Math.max(0, remaining - (performance.now() - startedAt)) : remaining;
  valueNode.textContent = formatDuration(current, "MM:ss");
  if (running && current <= 0) { remaining = 0; running = false; }
  firstButton.textContent = current === 0 ? "+1min" : running ? "Pause" : "Resume";
  secondButton.textContent = current === 0 ? "+5min" : "Restart";
};

firstButton.addEventListener("click", () => {
  const current = remaining;
  if (current === 0) remaining = 60 * 1000;
  else if (!running) { startedAt = performance.now(); running = true; }
  else { remaining = Math.max(0, remaining - (performance.now() - startedAt)); running = false; }
  render();
});
secondButton.addEventListener("click", () => {
  remaining = 0;
  running = false;
  render();
});

render();
const timer = window.setInterval(render, 100);
onPageCleanup(root, () => window.clearInterval(timer));

void WIDGET_ID;

  return { destroy: () => destroyWidget(root) };
}

if (globalThis.__KHAOSSHELL_WIDGET_AUTOSTART__) mount(globalThis.document);
