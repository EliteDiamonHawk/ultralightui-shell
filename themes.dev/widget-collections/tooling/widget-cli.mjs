import { access, cp, mkdir, readdir, readFile, rm, writeFile } from "node:fs/promises";
import { existsSync } from "node:fs";
import { resolve, relative, dirname, basename } from "node:path";
import { fileURLToPath } from "node:url";
import { build as viteBuild } from "vite";

const ROOT = resolve(fileURLToPath(new URL("..", import.meta.url)));
const COLLECTION_ROOT = resolve(ROOT, "collections");
const DIST_ROOT = resolve(ROOT, "dist");

const parseArgs = (argv) => {
  const result = { _: [] };
  for (let index = 0; index < argv.length; index += 1) {
    const value = argv[index];
    if (!value.startsWith("--")) {
      result._.push(value);
      continue;
    }
    const [key, inline] = value.slice(2).split("=", 2);
    result[key] = inline ?? argv[index + 1] ?? "";
    if (inline === undefined) index += 1;
  }
  return result;
};

const readJson = async (file) => JSON.parse(await readFile(file, "utf8"));

const readCollections = async () => {
  const entries = await readdir(COLLECTION_ROOT, { withFileTypes: true });
  return Promise.all(entries.filter((entry) => entry.isDirectory()).sort((a, b) => a.name.localeCompare(b.name)).map(async (entry) => {
    const root = resolve(COLLECTION_ROOT, entry.name);
    const manifest = await readJson(resolve(root, "collection.json"));
    if (manifest.id !== entry.name) throw new Error(`Collection directory/manifest mismatch: ${entry.name}`);
    const widgetRoot = resolve(root, "src/widgets");
    const widgets = await readdir(widgetRoot, { withFileTypes: true });
    const records = await Promise.all(widgets.filter((widget) => widget.isDirectory()).sort((a, b) => a.name.localeCompare(b.name)).map(async (widget) => {
      const widgetRootPath = resolve(widgetRoot, widget.name);
      const widgetManifest = await readJson(resolve(widgetRootPath, "widget.json"));
      if (widgetManifest.collection !== manifest.id) {
        throw new Error(`${manifest.id}/${widget.name}: manifest collection must be ${manifest.id}.`);
      }
      return { ...widgetManifest, collection: manifest, collectionRoot: root, root: widgetRootPath };
    }));
    return { ...manifest, root, widgets: records };
  }));
};

const allWidgets = async () => (await readCollections()).flatMap((collection) => collection.widgets);

const selectWidgets = async (args) => {
  const collections = await readCollections();
  const collection = args.collection ? collections.find((item) => item.id === args.collection) : null;
  if (args.collection && !collection) throw new Error(`Unknown collection: ${args.collection}`);
  const widgets = collection ? collection.widgets : collections.flatMap((item) => item.widgets);
  const selected = args.widget ? widgets.filter((widget) => widget.id === args.widget || `${widget.collection.id}/${widget.id}` === args.widget) : widgets;
  if (args.widget && selected.length === 0) throw new Error(`Unknown widget: ${args.collection ? `${args.collection}/` : ""}${args.widget}`);
  return selected;
};

const outputNames = (asset) => {
  const name = basename(asset.name ?? "asset");
  if (name.endsWith(".css")) return "widget.css";
  if (asset.type === "asset") return `assets/${name}`;
  return "widget.js";
};

const buildOptions = (outDir) => ({
  configFile: false,
  base: "./",
  build: {
    outDir,
    emptyOutDir: true,
    rollupOptions: {
      input: resolve(outDir, "__missing.html"),
      output: {
        entryFileNames: "widget.js",
        chunkFileNames: "chunks/[name]-[hash].js",
        assetFileNames: outputNames,
      },
    },
  },
});

const copyManifest = async (widget, outDir, target) => {
  const { collection: ignoredCollection, collectionRoot: ignoredCollectionRoot, root: ignoredRoot, ...manifest } = widget;
  await writeFile(resolve(outDir, "widget.json"), JSON.stringify({
    ...manifest,
    collection: widget.collection.id,
    target,
  }, null, 2) + "\n");
};

const injectAutostart = (html) => html.replace(/(<script type="module"[^>]*>)/, "<script>globalThis.__KHAOSSHELL_WIDGET_AUTOSTART__ = true;</script>$1");

const extractBody = (html) => {
  const body = html.match(/<body[^>]*>([\s\S]*?)<\/body>/i)?.[1] ?? html;
  return body.replace(/\s*<link[^>]+stylesheet[^>]*>/gi, "").replace(/\s*<script[^>]*><\/script>/gi, "").replace(/\s*<script[^>]*src=[^>]*><\/script>/gi, "").trim() + "\n";
};

const buildWidget = async (widget, { targets = ["standalone", "embed"], outRoot = DIST_ROOT } = {}) => {
  const results = {};
  for (const target of targets) {
    const outDir = resolve(outRoot, target, widget.collection.id, widget.id);
    await mkdir(outDir, { recursive: true });
    const input = resolve(widget.root, "index.html");
    await viteBuild({
      ...buildOptions(outDir),
      root: widget.root,
      build: {
        ...buildOptions(outDir).build,
        rollupOptions: {
          ...buildOptions(outDir).build.rollupOptions,
          input,
        },
      },
    });

    const generatedIndex = resolve(outDir, "index.html");
    const html = await readFile(generatedIndex, "utf8");
    if (target === "standalone") {
      await writeFile(generatedIndex, injectAutostart(html));
    } else {
      await writeFile(resolve(outDir, "widget.html"), extractBody(html));
      await rm(generatedIndex);
      await viteBuild({
        configFile: false,
        root: widget.root,
        build: {
          outDir,
          emptyOutDir: false,
          lib: {
            entry: resolve(widget.root, "widget.js"),
            formats: ["es"],
            fileName: () => "widget.js",
          },
          rollupOptions: {
            output: {
              entryFileNames: "widget.js",
              chunkFileNames: "chunks/[name]-[hash].js",
              assetFileNames: outputNames,
            },
          },
        },
      });
    }
    await copyManifest(widget, outDir, target);
    results[target] = outDir;
  }
  return results;
};

const buildCollection = async (collection, widgets) => {
  const selected = widgets ?? collection.widgets;
  for (const widget of selected) await buildWidget(widget);
  const outDir = resolve(DIST_ROOT, "collections", collection.id);
  await viteBuild({
    configFile: false,
    root: resolve(collection.root, "src"),
    base: "./",
    build: {
      outDir,
      emptyOutDir: true,
      rollupOptions: { input: resolve(collection.root, "src/index.html") },
    },
  });
  for (const widget of selected) {
    await cp(resolve(DIST_ROOT, "standalone", collection.id, widget.id), resolve(outDir, "widgets", widget.id), { recursive: true, force: true });
  }
  return outDir;
};

const verifyWidget = (widget) => {
  const required = ["index.html", "widget.css", "widget.js", "widget.json", "README.md"];
  return Promise.all(required.map(async (file) => {
    if (!existsSync(resolve(widget.root, file))) throw new Error(`${widget.collection.id}/${widget.id}: missing ${file}`);
  })).then(async () => {
    if (!widget.collection?.id) throw new Error(`${widget.id}: invalid collection metadata.`);
    if (widget.id !== basename(widget.root)) throw new Error(`${widget.id}: manifest id mismatch.`);
    if (!/^[a-z0-9-]+$/.test(widget.id)) throw new Error(`${widget.id}: invalid widget id.`);
    for (const field of ["title", "function"]) {
      if (!widget[field]) throw new Error(`${widget.id}: missing manifest field ${field}.`);
    }
    if (widget.settings !== undefined) {
      if (!Array.isArray(widget.settings)) throw new Error(`${widget.id}: settings must be an array.`);
      const keys = new Set();
      for (const setting of widget.settings) {
        if (!setting || typeof setting !== "object" || !/^[a-z][a-zA-Z0-9]*$/.test(setting.key ?? "")) {
          throw new Error(`${widget.id}: each setting needs a camelCase key.`);
        }
        if (keys.has(setting.key)) throw new Error(`${widget.id}: duplicate setting ${setting.key}.`);
        keys.add(setting.key);
        if (!["string", "integer", "number", "boolean", "object"].includes(setting.type)) {
          throw new Error(`${widget.id}/${setting.key}: unsupported setting type ${setting.type}.`);
        }
        if (setting.enum !== undefined && (!Array.isArray(setting.enum) || setting.enum.length === 0)) {
          throw new Error(`${widget.id}/${setting.key}: enum must be a non-empty array.`);
        }
        if (setting.enum !== undefined) {
          const enumMatchesType = setting.enum.every((value) => setting.type === "string" ? typeof value === "string"
            : setting.type === "integer" ? Number.isInteger(value)
              : setting.type === "number" ? typeof value === "number" && Number.isFinite(value)
                : setting.type === "boolean" ? typeof value === "boolean"
                  : value && typeof value === "object" && !Array.isArray(value));
          if (!enumMatchesType) throw new Error(`${widget.id}/${setting.key}: enum values do not match type ${setting.type}.`);
        }
        if (setting.minimum !== undefined && (!["integer", "number"].includes(setting.type) || typeof setting.minimum !== "number" || !Number.isFinite(setting.minimum))) throw new Error(`${widget.id}/${setting.key}: minimum must be numeric for numeric settings.`);
        if (setting.maximum !== undefined && (!["integer", "number"].includes(setting.type) || typeof setting.maximum !== "number" || !Number.isFinite(setting.maximum))) throw new Error(`${widget.id}/${setting.key}: maximum must be numeric for numeric settings.`);
        if (setting.minimum !== undefined && setting.maximum !== undefined && setting.minimum > setting.maximum) throw new Error(`${widget.id}/${setting.key}: invalid range.`);
        if (setting.properties !== undefined && (typeof setting.properties !== "object" || setting.properties === null || Array.isArray(setting.properties))) {
          throw new Error(`${widget.id}/${setting.key}: properties must be an object.`);
        }
        if (setting.properties !== undefined && setting.type !== "object") throw new Error(`${widget.id}/${setting.key}: properties require object type.`);
        if (setting.default !== undefined && setting.default !== null) {
          const matchesType = setting.type === "string" ? typeof setting.default === "string"
            : setting.type === "integer" ? Number.isInteger(setting.default)
              : setting.type === "number" ? typeof setting.default === "number" && Number.isFinite(setting.default)
                : setting.type === "boolean" ? typeof setting.default === "boolean"
                  : typeof setting.default === "object" && !Array.isArray(setting.default);
          if (!matchesType) throw new Error(`${widget.id}/${setting.key}: default does not match type ${setting.type}.`);
          if (setting.minimum !== undefined && setting.default < setting.minimum) throw new Error(`${widget.id}/${setting.key}: default is below minimum.`);
          if (setting.maximum !== undefined && setting.default > setting.maximum) throw new Error(`${widget.id}/${setting.key}: default is above maximum.`);
          if (setting.enum !== undefined && !setting.enum.includes(setting.default)) throw new Error(`${widget.id}/${setting.key}: default is not in enum.`);
        }
      }
    }
    const html = await readFile(resolve(widget.root, "index.html"), "utf8");
    const script = await readFile(resolve(widget.root, "widget.js"), "utf8");
    if (!html.includes("./widget.js") || !html.includes("./widget.css")) throw new Error(`${widget.id}: HTML must load local widget assets.`);
    if (!/export function mount\(/.test(script)) throw new Error(`${widget.id}: widget.js must export mount(root, options).`);
    if (/\.\.\/\.\.\/common\//.test(script)) throw new Error(`${widget.id}: widget.js still uses collection-local common imports.`);
    if (!/const SCALE\s*=/.test(script)) throw new Error(`${widget.id}: missing SCALE constant.`);
  });
};

const verify = async (args) => {
  const widgets = await selectWidgets(args);
  const seen = new Set();
  for (const widget of await allWidgets()) {
    const key = `${widget.collection.id}/${widget.id}`;
    if (seen.has(key)) throw new Error(`Duplicate widget coordinate: ${key}`);
    seen.add(key);
  }
  for (const widget of widgets) await verifyWidget(widget);
  console.log(`Verified ${widgets.length} widget${widgets.length === 1 ? "" : "s"}.`);
};

const catalog = async () => {
  const collections = await readCollections();
  const output = collections.map(({ root, widgets, ...collection }) => ({
    ...collection,
    widgets: widgets.map(({ collection: ignored, collectionRoot, root: widgetRoot, ...widget }) => widget),
  }));
  await writeFile(resolve(ROOT, "catalog.json"), JSON.stringify({ version: 1, collections: output }, null, 2) + "\n");
  console.log(`Cataloged ${output.reduce((sum, collection) => sum + collection.widgets.length, 0)} widgets across ${output.length} collections.`);
};

const main = async () => {
  const args = parseArgs(process.argv.slice(2));
  const command = args._[0] ?? "list";
  if (command === "list") {
    for (const collection of await readCollections()) {
      console.log(`${collection.id} (${collection.widgets.length})`);
      for (const widget of collection.widgets) console.log(`  ${collection.id}/${widget.id} - ${widget.title}`);
    }
    return;
  }
  if (command === "catalog") return catalog();
  if (command === "verify") return verify(args);
  if (command === "build") {
    for (const widget of await selectWidgets(args)) await buildWidget(widget);
    return;
  }
  if (command === "export") {
    const widgets = await selectWidgets(args);
    if (widgets.length !== 1) throw new Error("widget:export requires exactly one widget.");
    await buildWidget(widgets[0], { outRoot: resolve(ROOT, args.out || "exports") });
    return;
  }
  if (command === "collection-build") {
    const collections = await readCollections();
    const collection = collections.find((item) => item.id === args.collection);
    if (!collection) throw new Error("collection:build requires a valid --collection.");
    await buildCollection(collection);
    return;
  }
  if (command === "build-all") {
    for (const collection of await readCollections()) await buildCollection(collection);
    return;
  }
  throw new Error(`Unknown command: ${command}`);
};

await main();
