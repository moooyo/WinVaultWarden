import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";

const manifestFiles = ["manifest.json", "manifest.edge.json"];

for (const file of manifestFiles) {
  test(`${file} declares MAIN and ISOLATED worlds only`, async () => {
    const manifest = JSON.parse(await readFile(new URL(`../${file}`, import.meta.url)));

    assert.equal(manifest.manifest_version, 3);
    assert.deepEqual(
      manifest.content_scripts.map((entry) => entry.world),
      ["MAIN", "ISOLATED"],
    );

    for (const script of manifest.content_scripts) {
      assert.equal(script.run_at, "document_start");
      assert.equal(script.all_frames, true);
      assert.deepEqual(script.matches, ["<all_urls>"]);
    }

    // The page-world shim, its coordinator and its error map inject into MAIN. The isolated relay
    // must NOT list the same physical script as MAIN: real Chrome/Edge dedupe an identical script
    // path across content_scripts entries and only inject it into the first world that claims it,
    // so a shared `src/request-coordinator.js` entry silently never runs in ISOLATED.
    const [main, isolated] = manifest.content_scripts;
    assert.deepEqual(main.js, [
      "src/error-map.js",
      "src/request-coordinator.js",
      "src/page-webauthn.js",
    ]);
    assert.deepEqual(isolated.js, ["src/content-webauthn.js"]);
    for (const script of main.js) {
      assert.ok(!isolated.js.includes(script), `ISOLATED must not reuse MAIN script ${script}`);
    }

    // No dynamic script injection surface, and no popup/options/autofill scope creep.
    assert.equal(manifest.web_accessible_resources, undefined);
    assert.equal(manifest.action, undefined);
    assert.equal(manifest.options_page, undefined);
    assert.equal(manifest.options_ui, undefined);

    // Passkey bridge stays headless: no stored-password surface anywhere in the manifest.
    assert.doesNotMatch(JSON.stringify(manifest), /password/iu);

    // Native messaging stays the only permission; the persistent port needs it.
    assert.deepEqual(manifest.permissions, ["nativeMessaging"]);
    assert.equal(manifest.background.service_worker, "src/background.js");
    assert.equal(manifest.background.type, "module");
  });
}

test("Chrome and Edge keep independent, distinct manifest keys", async () => {
  const chrome = JSON.parse(await readFile(new URL("../manifest.json", import.meta.url)));
  const edge = JSON.parse(await readFile(new URL("../manifest.edge.json", import.meta.url)));

  assert.equal(typeof chrome.key, "string");
  assert.equal(typeof edge.key, "string");
  assert.notEqual(chrome.key, edge.key);
});

test("content-webauthn.js never injects a dynamic <script> element", async () => {
  const source = await readFile(new URL("../src/content-webauthn.js", import.meta.url), "utf8");

  assert.doesNotMatch(source, /createElement\s*\(\s*["']script["']/u);
  assert.doesNotMatch(source, /getURL\s*\(/u);
  // The relay talks over a runtime port, never chrome.runtime.sendMessage.
  assert.doesNotMatch(source, /runtime\.sendMessage/u);
  assert.match(source, /chrome\.runtime\.connect\s*\(/u);
});

test("background.js uses a persistent native port, not one-shot sendNativeMessage", async () => {
  const source = await readFile(new URL("../src/background.js", import.meta.url), "utf8");

  assert.doesNotMatch(source, /sendNativeMessage/u);
  assert.match(source, /onConnect/u);
});
