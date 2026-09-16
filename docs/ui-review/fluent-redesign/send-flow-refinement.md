# Send list refinement

The list now exposes **Copy link** as a persistent text button with a 36-pixel hit area. It remains available for disabled Sends: copying a link is permitted, while the preview explains that recipients cannot currently open it. Edit and delete stay in the closed overflow menu and preserve the exact row identity.

Each cell has at most two lines. Item type, file size where relevant, and access count share the name metadata line. The Access column distinguishes current status from the audience restriction. **Available until** describes recipient access, while its secondary line explains retention. The two active fixtures expire at deletion on 22 September 2026 at 14:00; the disabled fixture says **Not available** and retains its separate deletion date. This does not imply all Send expiration and deletion dates are equal.

The wider copy action trades some name-column width for a frequent, recognizable action. At 960 pixels, scoped column sizing keeps actions visible; long names truncate instead of pushing controls off-screen. Type filters and their data attributes are unchanged. No scene, backend capability, clipboard operation, or new sharing action is introduced.

The final light and dark renders were visually reviewed at 1280 by 900 and 960 by 720. At the smaller size, all three Copy link buttons measure 102 by 36 pixels. Filtering, exact menu targets, initially closed menus, and the disabled-Send copy explanation passed the focused check in `send-flow-refinement-validation.json`, with no horizontal clipping, browser errors, or external requests.
