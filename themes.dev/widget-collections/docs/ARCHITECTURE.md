# Architecture

Each collection is a namespace inside the KhaosShell Widget Collections
workspace. A collection owns its directory page, widget manifests, markup,
collection-specific CSS, and widget assets under `src/widgets/`.

The workspace root owns shared bridge, formatting, lifecycle, build, and common
asset packages. Widget modules export `mount(root, options)` and keep markup
static. The workspace builder writes standalone and embeddable artifacts under
`dist/`.
