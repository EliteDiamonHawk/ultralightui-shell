import { destroyWidget, onPageCleanup } from "@khaosshell/widget-runtime/lifecycle.js";
import { applyWidgetTheme, readWidgetTheme } from "@khaosshell/widget-runtime/theme.js";
import { formatDuration } from "@khaosshell/widget-runtime/format.js";
import { booleanSetting, integerSetting, resolveSetting, stringSetting, widgetContext } from "@khaosshell/widget-runtime/settings.js";

const MAX_TIME = 59 * 60 * 1000 + 59 * 1000;

const SCALE = 1;

export function mount(root = globalThis.document, options = {}) {
  if (!root) throw new Error("KhaosShell widget mount requires a root element or document.");
  const { document, window, renderer } = widgetContext(root, options);
  const WIDGET_ID = "stopwatch";
  applyWidgetTheme(document, readWidgetTheme(WIDGET_ID, { primary: "#d6d6d6", secondary: "#2e2e2e", scale: SCALE }, renderer));
  const limit = resolveSetting({ root, widgetId: WIDGET_ID, options, renderer, key: "limit", defaultValue: MAX_TIME, validate: integerSetting({ minimum: 0, maximum: MAX_TIME }) });
  const syntax = resolveSetting({ root, widgetId: WIDGET_ID, options, renderer, key: "syntax", defaultValue: "mm:ss", validate: stringSetting() });
  const label = resolveSetting({ root, widgetId: WIDGET_ID, options, renderer, key: "label", defaultValue: "Stopwatch", validate: stringSetting() });
  const buttons = resolveSetting({ root, widgetId: WIDGET_ID, options, renderer, key: "buttons", defaultValue: true, validate: booleanSetting });
  const valueNode = document.querySelector("[data-webskin-frame-part=\"value\"]");
  const subtextNode = document.querySelector("[data-webskin-frame-part=\"subtext\"]");
  const actionRoot = document.querySelector("[data-webskin-frame-part=\"actions\"]");
  const firstButton = document.querySelector("[data-timer-first]");
  const secondButton = document.querySelector("[data-timer-second]");
  subtextNode.textContent = label;
  actionRoot.hidden = !buttons;
  let elapsed = 0;
  let startedAt = 0;
  let running = false;
  const currentValue = () => running ? Math.min(limit, elapsed + performance.now() - startedAt) : elapsed;
  const render = () => {
    const current = currentValue();
    valueNode.textContent = formatDuration(current, syntax);
    if (running && current >= limit) {
      elapsed = limit;
      running = false;
    }
    firstButton.textContent = running ? "Pause" : elapsed > 0 ? "Resume" : "Play";
    secondButton.textContent = "Restart";
  };
  const onFirstClick = () => {
    if (running) { elapsed = currentValue(); running = false; }
    else if (elapsed < limit) { startedAt = performance.now(); running = true; }
    render();
  };
  const onSecondClick = () => {
    elapsed = 0;
    running = false;
    render();
  };
  firstButton.addEventListener("click", onFirstClick);
  secondButton.addEventListener("click", onSecondClick);
  render();
  const timer = window.setInterval(render, 100);
  onPageCleanup(root, () => {
    firstButton.removeEventListener("click", onFirstClick);
    secondButton.removeEventListener("click", onSecondClick);
    window.clearInterval(timer);
  });
  return { destroy: () => destroyWidget(root) };
}

if (globalThis.__KHAOSSHELL_WIDGET_AUTOSTART__) mount(globalThis.document);
