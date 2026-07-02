import { execFile } from "node:child_process";
import { readdir, readFile } from "node:fs/promises";
import { basename, relative, resolve, sep } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import { promisify } from "node:util";

const execFileAsync = promisify(execFile);
const extensionRoot = fileURLToPath(new URL(".", import.meta.url));

export async function validateExtensions(root = extensionRoot) {
  const resolvedRoot = resolve(root);
  const files = await listFiles(resolvedRoot);
  const manifests = files.filter((file) => basename(file) === "copilot-extension.json");
  const mjsFiles = files.filter((file) => file.endsWith(".mjs"));
  const imported = [];

  if (manifests.length === 0) {
    throw new Error(`${formatPath(resolvedRoot, resolvedRoot)}: no copilot-extension.json manifests found`);
  }

  for (const manifest of manifests) {
    await validateManifest(resolvedRoot, manifest);
  }

  for (const file of mjsFiles) {
    await checkSyntax(resolvedRoot, file);
  }

  for (const file of mjsFiles) {
    const source = await readFile(file, "utf8");
    const relativePath = formatPath(resolvedRoot, file);
    if (!shouldDynamicallyImport(relativePath, source)) {
      continue;
    }

    await import(`${pathToFileURL(file).href}?validate=${process.pid}-${imported.length}`);
    imported.push(relativePath);
  }

  return {
    manifests: manifests.length,
    checked: mjsFiles.length,
    imported,
  };
}

export async function runExtensionTests(root = extensionRoot) {
  const resolvedRoot = resolve(root);
  const files = await listFiles(resolvedRoot);
  const tests = files
    .filter((file) => file.endsWith(".test.mjs"))
    .map((file) => formatPath(resolvedRoot, file))
    .sort();

  if (tests.length === 0) {
    return { tests: 0 };
  }

  try {
    await execFileAsync(process.execPath, ["--test", "--test-concurrency=1", ...tests], { cwd: resolvedRoot });
  } catch (error) {
    // node:test writes the failing-test report (assertion diffs, stack traces) to the
    // child's stdout as it runs; execFileAsync only rejects with an opaque
    // "Command failed: node --test ..." message. Surface the captured stdout (and any
    // stderr) so the actual test failure is visible instead of the bare exit code.
    // stdout first because that is where the failure detail lives.
    const detail = [error.stdout, error.stderr].filter(Boolean).join("\n").trim();
    throw new Error(`extension tests failed${detail ? `\n${detail}` : ""}`);
  }
  return { tests: tests.length };
}

export function shouldDynamicallyImport(relativePath, source) {
  const normalized = relativePath.replaceAll("\\", "/");

  if (normalized === "validate-extensions.mjs" || normalized.endsWith(".test.mjs")) {
    return false;
  }

  // Canvas entrypoints call joinSession at module scope and depend on the Copilot
  // host SDK being injected by the extension runtime. Syntax-check them, but only
  // import leaf/support modules that are safe in a plain Node process.
  if (source.includes("@github/copilot-sdk/extension") || source.includes("joinSession(")) {
    return false;
  }

  return true;
}

async function validateManifest(root, manifest) {
  const relativePath = formatPath(root, manifest);
  let parsed;

  try {
    parsed = JSON.parse(await readFile(manifest, "utf8"));
  } catch (error) {
    throw new Error(`${relativePath}: invalid JSON: ${error.message}`);
  }

  if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) {
    throw new Error(`${relativePath}: manifest must be a JSON object`);
  }

  if (typeof parsed.name !== "string" || parsed.name.trim().length === 0) {
    throw new Error(`${relativePath}: "name" must be a non-empty string`);
  }

  if (!Number.isInteger(parsed.version) || parsed.version <= 0) {
    throw new Error(`${relativePath}: "version" must be a positive integer`);
  }
}

async function checkSyntax(root, file) {
  try {
    await execFileAsync(process.execPath, ["--check", file], { cwd: root });
  } catch (error) {
    const detail = [error.stderr, error.stdout].filter(Boolean).join("\n").trim();
    throw new Error(`${formatPath(root, file)}: node --check failed${detail ? `\n${detail}` : ""}`);
  }
}

async function listFiles(root) {
  const files = [];

  async function walk(directory) {
    const entries = await readdir(directory, { withFileTypes: true });
    entries.sort((a, b) => a.name.localeCompare(b.name));

    for (const entry of entries) {
      const fullPath = resolve(directory, entry.name);
      if (entry.isDirectory()) {
        if (entry.name === "node_modules" || entry.name.startsWith(".test-")) {
          continue;
        }

        await walk(fullPath);
      } else if (entry.isFile()) {
        files.push(fullPath);
      }
    }
  }

  await walk(root);
  return files;
}

function formatPath(root, file) {
  const formatted = relative(root, file).split(sep).join("/");
  return formatted || ".";
}

if (process.argv[1] && fileURLToPath(import.meta.url) === resolve(process.argv[1])) {
  try {
    const validation = await validateExtensions();
    const tests = await runExtensionTests();
    console.log(`Validated ${validation.manifests} manifest(s), ${validation.checked} module(s), imported ${validation.imported.length} safe module(s), ran ${tests.tests} test file(s).`);
  } catch (error) {
    console.error(error.message);
    process.exitCode = 1;
  }
}
