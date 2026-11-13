# Application appearance

The account menu opens `/appearance` and can switch directly between Light and Dark.
The page saves mode and preset selections as they are chosen. The color picker and HEX
field update the miniature preview as you edit. **Apply custom color** saves that color
and applies it across the app. **Restore defaults** selects System and Classic Blue
(`#176B9C`).

## Shared CSS

[theme.css](../src/Confast.Web/wwwroot/theme.css) defines the palette on the document
root. Razor-isolated styles inherit the same variables as the shared styles.

| Use | Tokens |
| --- | --- |
| Page, cards, sidebar, dialogs and fields | `--page-bg`, `--surface`, `--sidebar-bg`, `--surface-raised`, `--surface-muted`, `--surface-inset`, `--input-bg` |
| Body, labels and help text | `--text`, `--text-strong`, `--text-label`, `--text-secondary`, `--text-muted` |
| Borders, hover and focus | `--border`, `--border-subtle`, `--input-border`, `--hover-bg`, `--row-hover`, `--focus-ring` |
| Header and primary actions | `--header`, `--on-header`, `--accent`, `--on-accent`, `--accent-hover`, `--accent-active` |
| Links, selection and interactive borders | `--accent-text`, `--accent-text-hover`, `--accent-soft`, `--accent-selected`, `--accent-highlight`, `--accent-border` |
| Business states and department icons | `--success-*`, `--warning-*`, `--danger-*`, `--info-*`, `--department-*` |

Use the existing `.button-primary`, `.button-secondary`, `.form-field`, `.data-table`,
`.status-pill` and `.alert` styles where they fit. Use `--on-accent` for text or icons
on an accent fill and `--accent-text` for links on ordinary surfaces. Use
`--input-border` where the boundary identifies an editable control. Pair status colors
with a label, icon or other cue. Keep department colors on the category and shortcut
icons. The active destination uses the user's accent.

The PDF viewer inherits the host palette in a same-origin frame. It applies the palette
to the toolbar and surrounding surfaces. The canvas and printable inspection sheets
retain their document colors. The rich-text toolbar and its menus use the theme, while
the email editing and preview surfaces show authored text colors on paper backgrounds.

## Rendering and contrast

The outer Blazor document reads the account's stored preference before rendering the
head. A small head script resolves System with `matchMedia` and sets `data-theme` before
the styles paint. Explicit modes take precedence over the operating system. The system
listener and document palette live for the lifetime of the browser page, including
ordinary Blazor navigation and circuit reconnection. Browser storage and preference
cookies are unnecessary.

[theme.js](../src/Confast.Web/wwwroot/theme.js) applies custom variants once per
color and resolved mode. It calculates sRGB relative luminance, picks a black or white
foreground for accent fills, and adjusts text, focus and action variants against
representative surfaces. Buttons and their hover and active states target 4.5:1 for
labels and 3:1 for meaningful boundaries. Darker or lighter variants keep links
readable independently of the primary button fill. CSS mixes the derived accent into
soft and selected surfaces. Check the accent contrast tests when changing the shared
surface colors or adding a stronger selection tint.
[W3C text contrast guidance](https://www.w3.org/WAI/WCAG22/Understanding/contrast-minimum.html)
[W3C non-text contrast guidance](https://www.w3.org/WAI/WCAG22/Understanding/non-text-contrast.html)

## Persistence and deployment

`AddAppearancePreferences` adds nullable `appearance_mode` and `accent_color` columns
to the existing `navigation_preferences` table. Run the normal EF migration before
starting the updated app. Missing or obsolete values resolve independently to their
defaults. The same runtime grants and configuration apply.

The preference service uses the current Identity account and validates the active
session. Each write merges the fields being edited into the current database row.
Concurrent edits to different fields compose, and the last committed edit to the same
field wins. Opening the Appearance page or starting a new session reads current
preferences. Restore Defaults changes both appearance fields in one write.
