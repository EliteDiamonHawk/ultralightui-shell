# KhaosShell Widget Collections

This directory is a pnpm workspace containing four separated widget
collections and shared build/runtime packages.

KhaosShell is the application as a whole. Aether is the renderer used by
KhaosShell and the communication surface exposed to widgets.

## Collections

- `basic` — minimal plain WebSkin widgets.
- `end4-pc-remake` — end4-pc-remake visual references.
- `paper-charcoal` — current Paper-Charcoal widgets.
- `other` — widgets outside the named collections.

Each widget is addressed as `<collection>/<widget-id>`, for example
`paper-charcoal/paper-battery-meter`.

## Commands

Install dependencies once:

    pnpm install

List and verify widgets:

    pnpm widget:list
    pnpm widget:verify
    pnpm widget:verify --collection paper-charcoal --widget paper-battery-meter

Build one widget, a collection, or everything:

    pnpm widget:build --collection paper-charcoal --widget paper-battery-meter
    pnpm collection:build --collection paper-charcoal
    pnpm build

Build outputs are written under `dist/standalone/<collection>/<widget>` and
`dist/embed/<collection>/<widget>`. Standalone output is a runnable page.
Embed output contains static `widget.html`, `widget.css`, `widget.js`,
`widget.json`, and only the assets required by that widget.

The embeddable module exports `mount(root, options)` and returns an object with
`destroy()`. It expects the static markup from `widget.html` in the supplied
root element.

Shared runtime code lives in `packages/widget-runtime`; common assets live in
`packages/widget-assets`; reusable visual frames live in
`packages/widget-style-primitives`.

Shared workspace documentation lives in `docs/`.
