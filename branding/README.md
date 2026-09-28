# Branding

Put the company's visual identity here. The installer copies this folder to
`%APPDATA%\ACE-RevitMCP\branding`, and the Revit add-in reads it when Revit starts.

| Setting in `brand.json` | Used for |
|---|---|
| `name` | Title of the Companion panel ("ACE Companion") |
| `primary` | Ribbon icon background, panel accent bar, main buttons (light theme) |
| `accent` | Status dot on the icons, main buttons (dark theme) |
| `fullName` | Line under the panel title, e.g. `ACE \| AlAin Consulting Engineers` |
| `greyDark` / `greyLight` / `font` | Secondary text colour, panel background, and font |
| `mark` | The company lettermark only (e.g. just "ACE"): the official black artwork on a transparent background. Shown in the panel header on a white logo field, at 72 px wide or more. Never recoloured. |
| `logo` | File name of a full logo in this folder, e.g. `logo.png` (transparent PNG, about 256 px tall). Shown in the panel header. |
| `useLogoOnRibbon` | `true` to use the logo as the ribbon button icon instead of the drawn icons. Use a square logo mark for this. |

Colours are hex codes (`#RRGGBB`). Replace the defaults with the official ACE brand colours and logo
from the marketing / brand guidelines. Then rebuild the package (`build-package.ps1`), or copy the
files to `%APPDATA%\ACE-RevitMCP\branding` and restart Revit.

**ACE's lettermark file (`ace-mark.png`) and `ACE_BRAND_GUIDELINES.md` are not stored in the public GitHub
repository.** Put them in this folder on the machine that builds the package. They were made from the
official logo used in ACE project title blocks (the lettermark only, with a transparent background).
Colours follow ACE_BRAND_GUIDELINES.md: black, white and greys, with ACE Red `#C8102E` as an accent only. Items marked TBC there (reversed logo, official font, secondary palette) should be confirmed with ACE Marketing.
