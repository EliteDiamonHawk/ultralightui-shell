# Architecture

This collection is one namespace inside the root KhaosShell Widget Collections
workspace. It owns its directory page, widget manifests, markup, collection
specific CSS, and widget assets under `src/widgets/`.

Shared bridge, formatting, lifecycle, build, and common asset packages live at
the workspace root. Widget modules export `mount(root, options)` and keep their
markup static. The workspace builder produces standalone and embeddable
artifacts under `dist/`.
