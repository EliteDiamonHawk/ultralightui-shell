import assert from "node:assert/strict";
import test from "node:test";

import { openNyxMetricsStream } from "../src/bridge.js";
import { formatDuration } from "../src/format.js";
import { destroyWidget, onPageCleanup } from "../src/lifecycle.js";
import { monthGrid, normalizeDate } from "../src/calendar.js";
import {
  deriveNetworkTransfer,
  formatMetricBytesParts,
  formatMetricNumber,
  formatMetricTemplate,
  startNyxMetricWidget,
} from "../src/metrics.js";
import {
  MAX_INTERVAL_SECONDS,
  dateSetting,
  integerSetting,
  nyxIntervalSetting,
  positiveIntervalSetting,
  resolveSetting,
  widgetContext,
} from "../src/settings.js";
import { applyWidgetTheme, readWidgetTheme } from "../src/theme.js";

const warningRoot = () => ({ documentElement: { dataset: {} } });

const styleFor = () => {
  const values = new Map();
  return {
    values,
    setProperty: (name, value) => values.set(name, String(value)),
    getPropertyValue: (name) => values.get(name) ?? "",
  };
};

const embeddedPage = () => {
  const view = {
    addEventListener: () => {},
    removeEventListener: () => {},
  };
  const pageDocument = {
    nodeType: 9,
    defaultView: view,
    documentElement: { nodeType: 1, dataset: {}, style: styleFor() },
  };
  const root = () => ({
    nodeType: 1,
    ownerDocument: pageDocument,
    dataset: {},
    style: styleFor(),
    querySelector: () => null,
    querySelectorAll: () => [],
  });
  return { pageDocument, root };
};

test("settings prefer explicit options and safely fall back for invalid Aether values", () => {
  const root = warningRoot();
  const renderer = { aether: { variables: { get: () => 99 } } };
  const descriptor = {
    root,
    widgetId: "example",
    key: "count",
    options: {},
    renderer,
    defaultValue: 5,
    validate: integerSetting({ minimum: 0, maximum: 10 }),
  };

  assert.equal(resolveSetting(descriptor), 5);
  assert.equal(root.documentElement.dataset.widgetConfigWarning, "count");
  assert.equal(resolveSetting({ ...descriptor, options: { count: 7 } }), 7);
  assert.throws(() => resolveSetting({ ...descriptor, options: { count: 11 } }), /at most 10/);
});

test("date validation and month grids support years below 100", () => {
  assert.equal(dateSetting("0000-02-29", "date"), "0000-02-29");
  assert.equal(dateSetting("0099-12-31", "date"), "0099-12-31");
  assert.throws(() => dateSetting("0001-02-29", "date"), /real calendar date/);

  const date = normalizeDate("0099-12-31");
  const grid = monthGrid(date, 0, 6);
  assert.equal(grid.filter((cell) => cell.inMonth).length, 31);
  assert.ok(grid.some((cell) => cell.date.getFullYear() === 99 && cell.date.getMonth() === 11 && cell.date.getDate() === 31));
});

test("Nyx metric intervals are limited to the backend-supported range", () => {
  assert.equal(nyxIntervalSetting(1, "updateInterval"), 1);
  assert.equal(nyxIntervalSetting(3600, "updateInterval"), 3600);
  assert.throws(() => nyxIntervalSetting(0, "updateInterval"), /at least 1/);
  assert.throws(() => nyxIntervalSetting(3601, "updateInterval"), /at most 3600/);
});

test("clock and calendar intervals stay within the host timer boundary", () => {
  assert.equal(positiveIntervalSetting(MAX_INTERVAL_SECONDS, "updateInterval"), MAX_INTERVAL_SECONDS);
  assert.throws(() => positiveIntervalSetting(MAX_INTERVAL_SECONDS + 1, "updateInterval"), /at most 2147483/);
  assert.ok(MAX_INTERVAL_SECONDS * 1000 <= 2_147_483_647);
});

test("embedded widget contexts isolate theme and configuration state", async () => {
  const page = embeddedPage();
  const first = { pageDocument: page.pageDocument, root: page.root() };
  const second = { pageDocument: page.pageDocument, root: page.root() };
  const firstRenderer = { aether: { variables: { get: (key) => key.endsWith("primary") ? "#111111" : key.endsWith("secondary") ? "#222222" : key.endsWith("scale") ? 2 : undefined } } };
  const secondRenderer = { aether: { variables: { get: (key) => key.endsWith("primary") ? "#eeeeee" : key.endsWith("secondary") ? "#dddddd" : key.endsWith("scale") ? 3 : undefined } } };

  const firstContext = widgetContext(first.root);
  const secondContext = widgetContext(second.root);
  applyWidgetTheme(firstContext.document, readWidgetTheme("example", { primary: "#d6d6d6", secondary: "#2e2e2e", scale: 1 }, firstRenderer));
  applyWidgetTheme(secondContext.document, readWidgetTheme("example", { primary: "#d6d6d6", secondary: "#2e2e2e", scale: 1 }, secondRenderer));

  assert.equal(firstContext.document.documentElement, first.root);
  assert.equal(secondContext.document.documentElement, second.root);
  assert.equal(first.root.style.getPropertyValue("--widget-primary"), "#111111");
  assert.equal(first.root.style.getPropertyValue("--widget-secondary"), "#222222");
  assert.equal(first.root.style.getPropertyValue("--widget-scale"), "2");
  assert.equal(second.root.style.getPropertyValue("--widget-primary"), "#eeeeee");
  assert.equal(second.root.style.getPropertyValue("--widget-secondary"), "#dddddd");
  assert.equal(second.root.style.getPropertyValue("--widget-scale"), "3");
  assert.equal(first.pageDocument.documentElement.style.getPropertyValue("--widget-primary"), "");

  resolveSetting({
    root: first.root,
    widgetId: "example",
    key: "count",
    renderer: { aether: { variables: { get: () => 99 } } },
    defaultValue: 5,
    validate: integerSetting({ minimum: 0, maximum: 10 }),
  });
  assert.equal(first.root.dataset.widgetConfigWarning, "count");
  assert.equal(second.root.dataset.widgetConfigWarning, undefined);

  let firstCleanupCalls = 0;
  let secondCleanupCalls = 0;
  onPageCleanup(first.root, () => { firstCleanupCalls += 1; });
  onPageCleanup(second.root, () => { secondCleanupCalls += 1; });
  await destroyWidget(first.root);
  assert.equal(firstCleanupCalls, 1);
  assert.equal(secondCleanupCalls, 0);
  assert.equal(second.root.style.getPropertyValue("--widget-primary"), "#eeeeee");
  await destroyWidget(second.root);

  const standalone = widgetContext(first.pageDocument);
  applyWidgetTheme(standalone.document, { primary: "#123456", secondary: "#654321", scale: 2 });
  assert.equal(first.pageDocument.documentElement.style.getPropertyValue("--widget-primary"), "#123456");
});

test("unavailable metric values render as placeholders instead of zero", () => {
  assert.equal(formatMetricNumber(null), "--");
  assert.equal(formatMetricNumber(undefined), "--");
  assert.deepEqual(formatMetricBytesParts(null), { value: "--", unit: "--" });
  assert.deepEqual(formatMetricBytesParts(undefined), { value: "--", unit: "--" });
  assert.equal(formatMetricTemplate("{value}%", { value: "--" }, true), "--");
  assert.equal(formatMetricTemplate("{value} °C", { value: "--" }, false), "-- °C");
});

test("duration formatting preserves literal words around timer tokens", () => {
  assert.equal(formatDuration(65_000, "minutes: ss"), "minutes: 05");
  assert.equal(formatDuration(3_725_000, "H hours, m minutes, s seconds"), "1 hours, 2 minutes, 5 seconds");
  assert.equal(formatDuration(3_725_000, "HH:mm:ss"), "01:02:05");
});

test("page cleanup removes its pagehide listener and is idempotent", async () => {
  const listeners = new Set();
  const view = {
    addEventListener: (_type, listener) => listeners.add(listener),
    removeEventListener: (_type, listener) => listeners.delete(listener),
  };
  const root = { ownerDocument: { defaultView: view } };
  let calls = 0;
  const cleanup = onPageCleanup(root, () => { calls += 1; });
  assert.equal(listeners.size, 1);
  await cleanup();
  await cleanup();
  assert.equal(calls, 1);
  assert.equal(listeners.size, 0);
});

test("network transfer derivation handles initial, increasing, reset, and unavailable samples", () => {
  const first = deriveNetworkTransfer({ network: { bytes_received: 100, bytes_sent: 200 } }, undefined, 1000);
  assert.equal(first.metrics.network.transfer.download_speed, undefined);
  assert.equal(first.previous.received, 100);

  const second = deriveNetworkTransfer({ network: { bytes_received: 150, bytes_sent: 250 } }, first.previous, 2000);
  assert.equal(second.metrics.network.transfer.download_speed, 50);
  assert.equal(second.metrics.network.transfer.upload_speed, 50);

  const reset = deriveNetworkTransfer({ network: { bytes_received: 25, bytes_sent: 50 } }, second.previous, 3000);
  assert.equal(reset.metrics.network.transfer.download_speed, 0);
  assert.equal(reset.metrics.network.transfer.upload_speed, 0);

  const unavailable = deriveNetworkTransfer({ network: { available: false } }, reset.previous, 4000);
  assert.equal(unavailable.metrics.network.transfer, undefined);
  assert.equal(unavailable.previous, undefined);
});

test("stream cleanup closes a stream that opens after widget destruction", async () => {
  let resolveOpen;
  let closeCalls = 0;
  const renderer = {
    request: async () => ({ status: 200, body: { cpu: { utilization: { overall: 1 } } } }),
    openStream: () => new Promise((resolve) => { resolveOpen = resolve; }),
    closeStream: async () => { closeCalls += 1; },
    aether: { onEvent: () => () => {} },
  };

  const cleanup = startNyxMetricWidget({
    root: warningRoot(),
    renderer,
    widgetId: "cpu-utilization",
    fields: ["cpu.utilization.overall"],
    interval: 5,
    render: () => {},
  });

  for (let attempt = 0; attempt < 20 && !resolveOpen; attempt += 1) {
    await new Promise((resolve) => setImmediate(resolve));
  }
  assert.equal(typeof resolveOpen, "function");
  await cleanup();
  resolveOpen({ streamId: "late-stream" });
  for (let attempt = 0; attempt < 20 && closeCalls === 0; attempt += 1) {
    await new Promise((resolve) => setImmediate(resolve));
  }
  assert.equal(closeCalls, 1);
});

test("stream events received before open resolves are replayed for the matching stream", async () => {
  let emit;
  let resolveOpen;
  let stopped = false;
  const snapshots = [];
  const renderer = {
    request: async () => ({ status: 200, body: {} }),
    openStream: () => new Promise((resolve) => { resolveOpen = resolve; }),
    closeStream: async () => {},
    aether: {
      onEvent: (listener) => {
        emit = listener;
        return () => { stopped = true; };
      },
    },
  };

  const opening = openNyxMetricsStream(["cpu.utilization.overall"], {
    renderer,
    onSnapshot: (snapshot) => snapshots.push(snapshot),
  });
  for (let attempt = 0; attempt < 20 && (!emit || !resolveOpen); attempt += 1) {
    await new Promise((resolve) => setImmediate(resolve));
  }
  emit({ source: "stream", backendId: "nyx", streamId: "early-stream", data: { json: JSON.stringify({ type: "snapshot", data: { value: 42 } }) } });
  emit({ source: "stream", backendId: "nyx", streamId: "other-stream", data: { json: JSON.stringify({ type: "snapshot", data: { value: 99 } }) } });
  resolveOpen({ streamId: "early-stream" });

  const cleanup = await opening;
  assert.deepEqual(snapshots, [{ value: 42 }]);
  await cleanup();
  assert.equal(stopped, true);
});
