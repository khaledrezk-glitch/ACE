# Branding

Put the company's visual identity here. The installer copies this folder to
`%APPDATA%\ACE-RevitMCP\branding`, and the Revit add-in reads it when Revit starts.

| Setting in `brand.json` | Used for |
|---|---|
| `name` | Title of the Companion panel ("ACE Companion") |
| `primary` | Ribbon icon background, panel accent bar, main buttons (light theme) |
| `accent` | Status dot on the icons, main buttons (dark theme) |
| `logo` | File name of a logo in this folder, e.g. `logo.png` (transparent PNG, about 256 px tall). Shown in the panel header. |
| `useLogoOnRibbon` | `true` to use the logo as the ribbon button icon instead of the drawn icons. Use a square logo mark for this. |

Colours are hex codes (`#RRGGBB`). Replace the defaults with the official ACE brand colours and logo
from the marketing / brand guidelines. Then rebuild the package (`build-package.ps1`), or copy the
files to `%APPDATA%\ACE-RevitMCP\branding` and restart Revit.
