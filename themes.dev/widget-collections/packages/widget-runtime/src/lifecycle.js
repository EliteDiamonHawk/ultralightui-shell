const registryFor = (root) => {
  if (!root) return new Set();
  if (!root.__khaosshellWidgetCleanups) {
    Object.defineProperty(root, "__khaosshellWidgetCleanups", { value: new Set(), configurable: true });
  }
  return root.__khaosshellWidgetCleanups;
};

export const onPageCleanup = (rootOrCleanup, maybeCleanup) => {
  const root = typeof rootOrCleanup === "function" ? null : rootOrCleanup;
  const cleanup = typeof rootOrCleanup === "function" ? rootOrCleanup : maybeCleanup;
  const registry = registryFor(root);
  let active = true;
  const run = async () => {
    if (!active) return;
    active = false;
    registry.delete(run);
    await cleanup?.();
  };
  registry.add(run);

  const pageDocument = root?.ownerDocument ?? globalThis.document;
  const view = pageDocument?.defaultView ?? globalThis.window;
  view?.addEventListener?.("pagehide", run, { once: true });
  return run;
};

export const destroyWidget = async (root) => {
  const registry = root?.__khaosshellWidgetCleanups;
  if (!registry) return;
  await Promise.all([...registry].map((cleanup) => cleanup()));
  registry.clear();
};

export const setText = (rootOrSelector, selectorOrValue, maybeValue) => {
  const root = typeof rootOrSelector === "string" ? globalThis.document : rootOrSelector;
  const selector = typeof rootOrSelector === "string" ? rootOrSelector : selectorOrValue;
  const value = typeof rootOrSelector === "string" ? selectorOrValue : maybeValue;
  const element = root?.querySelector?.(selector);
  if (element) element.textContent = String(value);
  return element;
};
