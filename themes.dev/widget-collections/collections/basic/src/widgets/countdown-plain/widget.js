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

const WIDGET_ID = "countdown-plain";
const SCALE = 1;
document.documentElement.style.setProperty("--widget-scale", String(SCALE));

const valueNode = document.querySelector('[data-basic-part="value"]');
const firstButton = document.querySelector('[data-basic-part="first-button"]');
const secondButton = document.querySelector('[data-basic-part="second-button"]');
let remaining = 0;
let startedAt = 0;
let running = false;

const render = () => {
  const current = running ? Math.max(0, remaining - (performance.now() - startedAt)) : remaining;
  if (running && current <= 0) {
    remaining = 0;
    running = false;
  }
  valueNode.textContent = formatDuration(current, "MM:ss");
  firstButton.textContent = current === 0 ? "+1min" : running ? "Pause" : "Resume";
  secondButton.textContent = current === 0 ? "+5min" : "Restart";
};

firstButton.addEventListener("click", () => {
  if (remaining === 0) remaining = 60_000;
  else if (!running) {
    startedAt = performance.now();
    running = true;
  } else {
    remaining = Math.max(0, remaining - (performance.now() - startedAt));
    running = false;
  }
  render();
});

secondButton.addEventListener("click", () => {
  remaining = remaining === 0 ? 5 * 60_000 : 0;
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
