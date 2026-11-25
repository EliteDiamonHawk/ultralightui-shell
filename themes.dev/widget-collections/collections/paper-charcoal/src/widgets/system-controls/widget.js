import { callNyxControl, hasAetherBridge } from "@khaosshell/widget-runtime/bridge.js";

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

const WIDGET_ID = "system-controls";
const SCALE = 1;
const CONTROL_NAMES = ["lock", "sleep", "logout", "restart", "shutdown"];

document.documentElement.style.setProperty("--widget-scale", String(SCALE));

const statusNode = document.querySelector("[data-status]");
const buttons = [...document.querySelectorAll("[data-control]")];

buttons.forEach((button) => {
  const name = button.dataset.control;
  button.addEventListener("click", async () => {
    if (!CONTROL_NAMES.includes(name)) return;
    if (!hasAetherBridge()) {
      statusNode.textContent = "preview only";
      return;
    }
    buttons.forEach((item) => item.setAttribute("disabled", ""));
    statusNode.textContent = name + " requested";
    try {
      await callNyxControl(name);
      statusNode.textContent = name + " sent to Nyx";
    } catch (error) {
      statusNode.textContent = error.message;
    } finally {
      buttons.forEach((item) => item.removeAttribute("disabled"));
    }
  });
});

void WIDGET_ID;

  return { destroy: () => destroyWidget(root) };
}

if (globalThis.__KHAOSSHELL_WIDGET_AUTOSTART__) mount(globalThis.document);
