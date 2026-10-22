# @passel/ui

Passel's design tokens as CSS custom properties: the Material 3 colour scheme in its light and dark variants (`--md-sys-color-*`) and the shape tokens (`--md-sys-shape-corner-*`), in `src/tokens.css`. The extension popup's stylesheet imports it (`@import "@passel/ui/tokens.css"`) and its bundler inlines it; the web app will do the same.

The mobile app gets the same scheme from Material You at runtime, with these values as its fallback (`lib/theme/`). Components shared between the popup and the web app will join the tokens here as the web app takes shape.
