import { hasAetherBridge, readNyxTime } from "@khaosshell/widget-runtime/bridge.js";
import { clockAngles, defaultDigitalSyntax, digitalValues, renderDigitalSyntax, resolveClockSettings, shiftedDate } from "@khaosshell/widget-runtime/clock.js";
import { destroyWidget, onPageCleanup } from "@khaosshell/widget-runtime/lifecycle.js";
import { applyWidgetTheme, readWidgetTheme } from "@khaosshell/widget-runtime/theme.js";
import { widgetContext } from "@khaosshell/widget-runtime/settings.js";

const SCALE = 1;

export function mount(root = globalThis.document, options = {}) {
  if (!root) throw new Error("KhaosShell widget mount requires a root element or document.");
  const { document, window, renderer } = widgetContext(root, options);
  const WIDGET_ID = "digital-and-analog-clock";
  applyWidgetTheme(document, readWidgetTheme(WIDGET_ID, { primary: "#d6d6d6", secondary: "#2e2e2e", scale: SCALE }, renderer));
  const settings = resolveClockSettings({ root, widgetId: WIDGET_ID, options, renderer, defaults: { format: "24h", seconds: false, showPeriod: true, smooth: true } });
  const digital = document.querySelector('[data-webskin-clock-part="digital"]');
  const hours = document.querySelector('[data-webskin-clock-part="hours"]');
  const minutes = document.querySelector('[data-webskin-clock-part="minutes"]');
  const seconds = document.querySelector('[data-webskin-clock-part="seconds"]');
  const secondsSeparator = document.querySelector('[data-webskin-clock-part="seconds-separator"]');
  const period = document.querySelector('[data-webskin-clock-part="period"]');
  const hourHand = document.querySelector('[data-webskin-clock-part="hour-hand"]');
  const minuteHand = document.querySelector('[data-webskin-clock-part="minute-hand"]');
  const secondHand = document.querySelector('[data-webskin-clock-part="second-hand"]');
  let epochOffset = 0;
  const syntax = settings.syntax ?? defaultDigitalSyntax(settings);
  const render = () => {
    const now = shiftedDate(new Date(Date.now() + epochOffset), settings.offsetMinutes);
    const values = digitalValues(now, settings.format);
    const angles = clockAngles(now, settings.smooth);
    hourHand.setAttribute("transform", `rotate(${angles.hour} 50 50)`);
    minuteHand.setAttribute("transform", `rotate(${angles.minute} 50 50)`);
    secondHand.setAttribute("transform", `rotate(${angles.second} 50 50)`);
    if (settings.syntax) digital.textContent = renderDigitalSyntax(syntax, values);
    else {
      hours.textContent = settings.format === "24h" ? values.HH : values.hh;
      minutes.textContent = values.mm;
      seconds.textContent = settings.seconds ? values.ss : "";
      seconds.hidden = !settings.seconds;
      secondsSeparator.hidden = !settings.seconds;
      period.hidden = settings.format !== "12h" || !settings.showPeriod;
      period.textContent = values.A;
    }
    digital.dateTime = now.toISOString();
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
