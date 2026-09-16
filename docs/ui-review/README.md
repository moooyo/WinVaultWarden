# WinVaultWarden UI review atlas

Open [index.html](index.html) in any current browser. The atlas is fully offline: it embeds its data, uses system fonts, and loads only the adjacent local PNG and SVG files.

The completed session contains **9 of 9 navigable pages captured**, **53 native screenshots** (including **2 demo-session captures**), and **178 logical source diagrams**. The [native page contact sheet](overview.png) presents one real screenshot per page. Read [runtime findings](runtime-findings.md), the [review session and reproduction notes](session.md), and the [backend and fixture setup](../../tools/UiReviewSeed/README.md) alongside the images.

Verification records are separate from capture coverage: [backend data checks](backend-verification.json), [offline atlas checks](atlas-validation.json), and [PNG pixel preservation](image-format-verification.json).

The gallery provides native application screenshots and source-derived logical diagrams. Blue evidence labels indicate native captures. Amber evidence labels indicate diagrams; their summarized English labels and logical control groups are not screenshots or exact pixel reproductions. Every source inventory entry receives a diagram. Capture coverage is reported separately, and does not claim that all states or external integrations have been tested. “Observed UI” is the display label for a real native capture; the manifest's `live` kind does not mean an end-to-end flow passed. Screenshot 13 records a blank editor defect, not a successfully rendered editor.

Use text search, area filtering, and evidence filtering to narrow the gallery. Open a card to inspect the image, zoom, read the source reference and entry point, or download the original. The coverage matrix always retains the complete inventory. The browser print command produces a landscape review document; printing preserves the currently filtered gallery and the complete coverage matrix.

## Inputs and generated artifacts

- `page-inventory.json` and `page-inventory.md`: source-based inventory maintained separately from the atlas generator.
- `screenshots/manifest.json`: native screenshot metadata. A capture has `id`, `title`, `group`, `filename`, `kind`, `notes`, and `source` fields. Optional `inventoryId`, `inventoryIds`, or `covers` values explicitly connect a capture to inventory entries.
- `capture-coverage.json`: per-capture mapping overrides merged by screenshot ID. `coverageNote` preserves what is visible and what remains unverified. `relatedInventoryIds` supplies context without contributing to capture coverage. The generator does not rewrite either input file.
- `screenshots/*.png`: original application captures. The generator verifies that every listed image exists and that its path stays inside the screenshot directory.
- `image-format-verification.json`: decoded-pixel hashes and exact before/after comparisons for the PNG format normalization. Native capture losslessly encoded as PNG: re-encoding preserves source pixels and dimensions; any JPEG compression already present in the capture source is not reversed.
- `diagrams/*.svg`: generated, explicitly labeled source-derived diagrams for each inventory entry and the application route map.
- `overview.png` and `overview.svg`: nine-page contact sheet, composed entirely from actual native captures. SVG embeds the image data; PNG is losslessly rasterized from the sheet.
- `index.html`: generated offline gallery, detail viewer, and complete coverage matrix.

Capture-to-inventory matching uses explicit inventory IDs, or an exactly matching normalized title only when no explicit mapping is present. It never treats one screenshot of a page as proof of every state in that page's source file. All mapped captures retain scope notes; related context links are not counted. Unmatched screenshots remain visible as standalone evidence.

## Rebuild

From the repository root, run:

```powershell
node scripts/ui-review/build-atlas.mjs
```

The generator requires a non-empty page inventory. It does not launch the application, provision services, collect screenshots, or run backend verification. Run it again after updating the inventory or screenshot manifest.

HTML and SVG generation uses only Node built-ins. PNG contact-sheet refresh also uses Sharp when available via normal module resolution, `UI_REVIEW_SHARP`, or the bundled Codex Windows runtime. Without Sharp, the builder refreshes the standalone SVG and retains an existing PNG, with a console notice.

## Review boundaries

Source diagrams summarize logical controls and their arrangement. They deliberately avoid reconstructing external provider pages, security-key system dialogs, or Windows Hello internals. Conditional and destructive flows may have diagram coverage without native captures. Native screenshots establish only the visible state at capture time; protocol, security, accessibility, and persistence behavior require separate testing.
