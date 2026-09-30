# Branding

Put the company's visual identity here. The installer copies this folder to
`%APPDATA%\ACE-RevitMCP\branding`, and the Revit add-in reads it when Revit starts.

| Setting in `brand.json` | Used for |
|---|---|
| `name` | Title of the Companion panel ("ACE Companion") |
| `primary` | Ribbon icon background, panel accent bar, main buttons (light theme) |
| `accent` | Status dot on the icons, main buttons (dark theme) |
| `fullName` | Line under the panel title, e.g. `ACE \| AlAin Consulting Engineers` |
| `greyDark` / `greyLight` / `font` | Secondary text colour, panel background, and body font |
| `headingFont` | Headline font of the reports (ACE: BW Gradual); Arial is used where a font is not installed |
| `mark` | The company lettermark only (e.g. just "ACE"): the official black artwork on a white or transparent background, cropped tightly (the panel adds the 25% clear space). Shown in the panel header on a white logo field, at 72 px wide or more. Never recoloured. |
| `logo` | File name of a full logo in this folder, e.g. `logo.png` (transparent PNG, about 256 px tall). Shown in the panel header. |
| `useLogoOnRibbon` | `true` to use the logo as the ribbon button icon instead of the drawn icons. Use a square logo mark for this. |

Colours are hex codes (`#RRGGBB`). Replace the defaults with the official ACE brand colours and logo
from the marketing / brand guidelines. Then rebuild the package (`build-package.ps1`), or copy the
files to `%APPDATA%\ACE-RevitMCP\branding` and restart Revit.

**ACE's lettermark file (`ace-mark.png`) and `ACE_BRAND_GUIDELINES.md` are not stored in the public GitHub
repository.** Put them in this folder on the machine that builds the package. They were cropped from the
official logo used in ACE project title blocks (the lettermark only, left of the divider). Crop from the largest
official file available (the current mark is 580 × 307 px); small copies look soft in the panel.
Colours and fonts follow ACE_BRAND_GUIDELINES.md (from the ACE Brand Guideline 07-04-2025 and Brand Card 10-7-2025):
Charcoal Black `#212121` (the guide advises subdued blacks rather than true black on screen), UAE Flag Red `#EF3340`
as an accent only, Grey `#414042`, Platinum `#E6E6E6` for backgrounds and Quick Silver `#A0A0A0` for rules.
Poppins for text and BW Gradual for headlines; install them on the PC from the ACE font files for the full look,
otherwise Arial is shown. Secondary colours are not used. Official logo files: the ACE logo folder linked in the Brand Card.
