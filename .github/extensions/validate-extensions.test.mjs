import assert from "node:assert/strict";
import { mkdir, rm, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import test from "node:test";

import { shouldDynamicallyImport, validateExtensions } from "./validate-extensions.mjs";

const fixturesRoot = fileURLToPath(new URL("../../artifacts/copilot-extension-validator-tests/", import.meta.url));

test.after(async () => {
  await rm(fixturesRoot, { recursive: true, force: true });
});

test("validateExtensions parses manifests, checks syntax, and imports safe modules", async () => {
  await resetFixtures();
  await writeFile(join(fixturesRoot, "sample", "copilot-extension.json"), JSON.stringify({ name: "sample", version: 1 }), "utf8");
  await writeFile(join(fixturesRoot, "sample", "safe.mjs"), "export const value = 42;\n", "utf8");
  await writeFile(join(fixturesRoot, "sample", "extension.mjs"), "import { joinSession } from '@github/copilot-sdk/extension';\nexport const value = joinSession;\n", "utf8");

  const result = await validateExtensions(fixturesRoot);

  assert.equal(result.manifests, 1);
  assert.equal(result.checked, 2);
  assert.deepEqual(result.imported.map((file) => file.replaceAll("\\", "/")), ["sample/safe.mjs"]);
});

test("validateExtensions rejects malformed manifests", async () => {
  await resetFixtures();
  await writeFile(join(fixturesRoot, "broken", "copilot-extension.json"), JSON.stringify({ name: "", version: "1" }), "utf8");

  await assert.rejects(
    validateExtensions(fixturesRoot),
    /copilot-extension\.json: "name" must be a non-empty string/,
  );
});

test("shouldDynamicallyImport skips extension entrypoints with host SDK side effects", () => {
  assert.equal(shouldDynamicallyImport("sample/safe.mjs", "export const value = 1;"), true);
  assert.equal(shouldDynamicallyImport("sample/server.test.mjs", "import test from 'node:test';"), false);
  assert.equal(shouldDynamicallyImport("sample/extension.mjs", "import { joinSession } from '@github/copilot-sdk/extension';"), false);
});

async function resetFixtures() {
  await rm(fixturesRoot, { recursive: true, force: true });
  await mkdir(join(fixturesRoot, "sample"), { recursive: true });
  await mkdir(join(fixturesRoot, "broken"), { recursive: true });
}
