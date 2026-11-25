# Adding a widget

1. Create `src/widgets/<slug>/` inside the collection.
2. Add `index.html`, `widget.css`, `widget.js`, `widget.json`, and `README.md`.
3. Keep markup static in `index.html`. Export `mount(root, options)` from the module.
4. Set the manifest `collection` to this collection ID and preserve `id`, `title`,
   `source`, `appearance`, `function`, and `legacyId`.
5. Import shared code from `@khaosshell/widget-runtime/*`. Do not import from relative collection paths.
6. Keep collection-specific assets under the widget's `assets/` directory.
7. Add the widget link to the collection's `src/index.html`.
8. Run these commands from the workspace root:

       pnpm widget:verify --collection <collection> --widget <slug>
       pnpm widget:build --collection <collection> --widget <slug>
