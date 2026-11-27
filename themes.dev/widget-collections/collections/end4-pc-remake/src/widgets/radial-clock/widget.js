import { hasAetherBridge, readNyxTime } from "@khaosshell/widget-runtime/bridge.js";
import { clockAngles, resolveClockSettings, shiftedDate } from "@khaosshell/widget-runtime/clock.js";
import { destroyWidget, onPageCleanup } from "@khaosshell/widget-runtime/lifecycle.js";
import { applyWidgetTheme, readWidgetTheme } from "@khaosshell/widget-runtime/theme.js";
import { widgetContext } from "@khaosshell/widget-runtime/settings.js";

const SCALE = 1;

export function mount(root = globalThis.document, options = {}) {
  if (!root) throw new Error("KhaosShell widget mount requires a root element or document.");
  const { document, window, renderer } = widgetContext(root, options);
  const WIDGET_ID = "radial-clock";
  applyWidgetTheme(document, readWidgetTheme(WIDGET_ID, { primary: "#d6d6d6", secondary: "#2e2e2e", scale: SCALE }, renderer));
  const settings = resolveClockSettings({ root, widgetId: WIDGET_ID, options, renderer, defaults: { smooth: true } });
  const hourHand = document.querySelector("[data-hour-hand]");
  const minuteHand = document.querySelector("[data-minute-hand]");
  const secondHand = document.querySelector("[data-second-hand]");
  let epochOffset = 0;
  const render = () => {
    const angles = clockAngles(shiftedDate(new Date(Date.now() + epochOffset), settings.offsetMinutes), settings.smooth);
    hourHand.setAttribute("transform", `rotate(${angles.hour} 50 50)`);
    minuteHand.setAttribute("transform", `rotate(${angles.minute} 50 50)`);
    secondHand.setAttribute("transform", `rotate(${angles.second} 50 50)`);
  };
  const syncTime = async () => {
    if (!hasAetherBridge(renderer)) return;
    try {
      const epoch = await readNyxTime(renderer);
      if (Number.isFinite(epoch)) epochOffset = epoch - Date.now();
    } catch {
    }
  };
  render();
  void syncTime();
  let animationFrame;
  let renderTimer;
  if (settings.smooth && typeof window.requestAnimationFrame === "function") {
    const animate = () => { render(); animationFrame = window.requestAnimationFrame(animate); };
    animationFrame = window.requestAnimationFrame(animate);
  } else {
    renderTimer = window.setInterval(render, settings.updateInterval * 1000);
  }
  const syncTimer = window.setInterval(syncTime, 60_000);
  onPageCleanup(root, () => {
    if (animationFrame !== undefined) window.cancelAnimationFrame(animationFrame);
    if (renderTimer !== undefined) window.clearInterval(renderTimer);
    window.clearInterval(syncTimer);
  });
  return { destroy: () => destroyWidget(root) };
}

if (globalThis.__KHAOSSHELL_WIDGET_AUTOSTART__) mount(globalThis.document);
