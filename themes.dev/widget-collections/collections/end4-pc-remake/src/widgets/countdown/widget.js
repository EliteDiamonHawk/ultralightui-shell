import { destroyWidget, onPageCleanup } from "@khaosshell/widget-runtime/lifecycle.js";
import { applyWidgetTheme, readWidgetTheme } from "@khaosshell/widget-runtime/theme.js";
import { formatDuration } from "@khaosshell/widget-runtime/format.js";
import { booleanSetting, integerSetting, resolveSetting, stringSetting, widgetContext } from "@khaosshell/widget-runtime/settings.js";

const MAX_TIME = 60 * 60 * 1000;
const MINUTE = 60 * 1000;

const SCALE = 1;

export function mount(root = globalThis.document, options = {}) {
  if (!root) throw new Error("KhaosShell widget mount requires a root element or document.");
  const { document, window, renderer } = widgetContext(root, options);
  const WIDGET_ID = "countdown";
  applyWidgetTheme(document, readWidgetTheme(WIDGET_ID, { primary: "#d6d6d6", secondary: "#2e2e2e", scale: SCALE }, renderer));
  const time = resolveSetting({ root, widgetId: WIDGET_ID, options, renderer, key: "time", defaultValue: 0, validate: integerSetting({ minimum: 0, maximum: MAX_TIME }) });
  const syntax = resolveSetting({ root, widgetId: WIDGET_ID, options, renderer, key: "syntax", defaultValue: "MM:ss", validate: stringSetting() });
  const label = resolveSetting({ root, widgetId: WIDGET_ID, options, renderer, key: "label", defaultValue: "Countdown", validate: stringSetting() });
  const buttons = resolveSetting({ root, widgetId: WIDGET_ID, options, renderer, key: "buttons", defaultValue: true, validate: booleanSetting });
  const valueNode = document.querySelector("[data-webskin-frame-part=\"value\"]");
  const subtextNode = document.querySelector("[data-webskin-frame-part=\"subtext\"]");
  const actionRoot = document.querySelector("[data-webskin-frame-part=\"actions\"]");
  const firstButton = document.querySelector("[data-timer-first]");
  const secondButton = document.querySelector("[data-timer-second]");
  subtextNode.textContent = label;
  actionRoot.hidden = !buttons;
  let remaining = time;
  let startedAt = 0;
  let running = false;
  let addingTime = remaining === 0;
  let addTimer;
  const currentValue = () => running ? Math.max(0, remaining - (performance.now() - startedAt)) : remaining;
  const render = () => {
    const current = currentValue();
    valueNode.textContent = formatDuration(current, syntax);
    if (running && current <= 0) {
      remaining = 0;
      running = false;
    }
    firstButton.textContent = current === 0 || addingTime ? "+1min" : running ? "Pause" : "Resume";
    secondButton.textContent = current === 0 || addingTime ? "+5min" : "Restart";
  };
  const addTime = (amount) => {
    const current = currentValue();
    remaining = Math.min(MAX_TIME, current + amount);
    running = false;
    addingTime = true;
    render();
    if (addTimer !== undefined) window.clearTimeout(addTimer);
    addTimer = window.setTimeout(() => { addTimer = undefined; addingTime = false; render(); }, 1000);
  };
  const onFirstClick = () => {
    const current = currentValue();
    if (current === 0 || addingTime) addTime(MINUTE);
    else if (running) { remaining = current; running = false; render(); }
    else { startedAt = performance.now(); running = true; render(); }
  };
  const onSecondClick = () => {
    const current = currentValue();
    if (current === 0 || addingTime) addTime(5 * MINUTE);
    else { remaining = 0; running = false; addingTime = true; render(); }
  };
  firstButton.addEventListener("click", onFirstClick);
  secondButton.addEventListener("click", onSecondClick);
  render();
  const timer = window.setInterval(render, 100);
  onPageCleanup(root, () => {
    firstButton.removeEventListener("click", onFirstClick);
    secondButton.removeEventListener("click", onSecondClick);
    window.clearInterval(timer);
    if (addTimer !== undefined) window.clearTimeout(addTimer);
  });
  return { destroy: () => destroyWidget(root) };
}

if (globalThis.__KHAOSSHELL_WIDGET_AUTOSTART__) mount(globalThis.document);
