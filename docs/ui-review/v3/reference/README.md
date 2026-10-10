# v3 design reference captures

These PNG files capture the supplied interactive HTML prototype. They are **design reference images only**, not screenshots of the WinUI implementation.

- Source: `D:/Code/design_handoff_winvaultwarden_v3/WinVaultWarden Prototype v3.dc.html`.
- Renderer: headless Microsoft Edge through the bundled Playwright runtime, device scale factor 1.
- Sizes: 1280 x 760, 1024 x 720, and 760 x 640 effective pixels, in light and dark themes.
- Seventy scenes: Vault, Generator, Security report, Account/Security settings, Send, selected login detail/editor and file Send detail/editor at all three widths/themes; plus text Send detail/editor, locked, sign-in, and registration at 1280 in both themes.
- The prototype's documented property bridge selects the preview size and disables decorative motion. The screenshot is cropped to the simulated application frame, excluding the surrounding preview controls.
- `manifest.json` records source, capture time, actual crop dimensions, scene, theme, file, and browser page errors. The script asserts the expected width before writing each capture.
- The design's demonstration data, generated values, TOTP countdown and dates remain prototype data; they do not come from a user vault.

Reproduce from the repository root using the bundled Node executable with `scripts/ui-review/render-v3-reference.mjs`. Native application evidence belongs in a separate directory and must be labeled as implementation evidence.
