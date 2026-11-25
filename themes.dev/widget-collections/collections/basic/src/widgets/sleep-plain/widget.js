import { callNyxControl, hasAetherBridge } from "@khaosshell/widget-runtime/bridge.js";
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

const WIDGET_ID = "sleep-plain";
const SCALE = 1;
document.documentElement.style.setProperty("--widget-scale", String(SCALE));

const CONTROL = "sleep";
const button = document.querySelector('[data-basic-part="control"]');
const status = document.querySelector('[data-basic-part="status"]');

const activate = async () => {
  if (!hasAetherBridge()) {
    status.textContent = "preview only";
    return;
  }
  button.disabled = true;
  status.textContent = `${CONTROL} requested`;
  try {
    await callNyxControl(CONTROL);
    status.textContent = `${CONTROL} sent to Nyx`;
  } catch (error) {
    status.textContent = error.message;
  } finally {
    button.disabled = false;
  }
};

button.addEventListener("click", activate);
onPageCleanup(root, () => button.removeEventListener("click", activate));

void WIDGET_ID;

  return { destroy: () => destroyWidget(root) };
}

if (globalThis.__KHAOSSHELL_WIDGET_AUTOSTART__) mount(globalThis.document);
