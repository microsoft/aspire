#!/usr/bin/env node

import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, renameSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { homedir, tmpdir } from 'node:os';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import process from 'node:process';

const DefaultNpmRegistry = 'https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public-npm/npm/registry/';
// Yarn 1.x `packageManager` strings can carry an integrity suffix when written
// by `corepack use yarn@<v>` (the workflow CONTRIBUTING.MD points contributors
// at to update the pin), producing values like
//   "yarn@1.22.22+sha512.f7062e6a5ee1f3aa…".
// The suffix is optional but its presence must not break us. Match an optional
// `+<token>` suffix and ignore it; only the version is needed to seed the cache.
//
// Integrity note: when Corepack's `installVersion` finds an existing cache dir
// containing a `.corepack` file, it returns the recorded `hash`/`bin`
// immediately without re-hashing or signature-checking the install (see
// https://github.com/nodejs/corepack/blob/v0.34.7/sources/corepackUtils.ts).
// Its `Mismatch hashes` check fires only on the download path, which a
// pre-seeded cache never reaches. That means the `sha1.${packEntry.shasum}` we
// write to `.corepack` below is recorded for completeness but is never
// re-verified on reuse — trust on the seeded Yarn rests entirely on the
// `npm pack` fetch from `$NPM_REGISTRY` (the internal dnceng feed), which is
// the same authentication and integrity boundary npm uses for any other
// install through this feed.
// Spec: https://nodejs.org/api/packages.html#packagemanager
const PackageManagerPattern = /^yarn@(?<version>\d+\.\d+\.\d+)(?:\+[\w.-]+)?$/;

const scriptDirectory = dirname(fileURLToPath(import.meta.url));
const extensionDirectory = dirname(scriptDirectory);
const packageJsonPath = join(extensionDirectory, 'package.json');

const packageJson = JSON.parse(readFileSync(packageJsonPath, 'utf8'));
const packageManager = packageJson.packageManager;
const match = typeof packageManager === 'string' ? PackageManagerPattern.exec(packageManager) : null;

if (match?.groups?.version === undefined) {
  fail(`Expected packageManager in ${packageJsonPath} to be an exact Yarn Classic version like "yarn@1.22.22", but found ${JSON.stringify(packageManager)}.`);
}

const yarnVersion = match.groups.version;
const majorVersion = Number(yarnVersion.split('.')[0]);

if (majorVersion >= 2) {
  fail(`The Corepack cache seeding workaround only supports Yarn Classic (<2.0.0), but packageManager is ${packageManager}. Remove this workaround and use Corepack's native prepare/install flow for Yarn Berry.`);
}

const registry = process.env.NPM_REGISTRY || DefaultNpmRegistry;
const corepackHome = getCorepackHome();
const installDirectory = join(corepackHome, 'v1', 'yarn', yarnVersion);
const installParentDirectory = dirname(installDirectory);
const corepackMetadataPath = join(installDirectory, '.corepack');

if (existsSync(corepackMetadataPath)) {
  console.log(`Corepack cache already contains yarn@${yarnVersion} at ${installDirectory}`);
  process.exit(0);
}

// Race-safe cleanup: only remove the install directory when it is stale
// (no `.corepack` metadata). The earlier existsSync at line 57 races against
// a concurrent winner whose renameSync could complete between that check and
// here; an unconditional rmSync would destroy that just-seeded cache. A
// narrow residual window remains between this re-check and the rmSync, but
// the rename at line 101 below would then fail with EEXIST/ENOTEMPTY and
// fall through to the existing concurrent-cache recovery path.
if (existsSync(installDirectory) && !existsSync(corepackMetadataPath)) {
  rmSync(installDirectory, { recursive: true, force: true });
}

const temporaryDirectory = mkdtempSync(join(tmpdir(), 'aspire-corepack-yarn-'));
mkdirSync(installParentDirectory, { recursive: true });
const stagingDirectory = mkdtempSync(join(installParentDirectory, `.yarn-${yarnVersion}-`));
let cacheSeeded = false;

try {
  console.log(`Packing yarn@${yarnVersion} from ${registry}`);
  const npm = getNpmInvocation();
  const packResult = run(npm.command, [...npm.args, 'pack', '--json', '--registry', registry, `yarn@${yarnVersion}`], temporaryDirectory);
  const packEntries = parseNpmPackJson(packResult.stdout);
  const packEntry = packEntries[0];

  if (packEntry === undefined || typeof packEntry.filename !== 'string' || typeof packEntry.shasum !== 'string') {
    fail(`npm pack did not return the expected filename and shasum metadata. Output: ${packResult.stdout}`);
  }

  const tarballPath = join(temporaryDirectory, packEntry.filename);

  // Corepack can use COREPACK_NPM_REGISTRY for npmjs.org, but Azure Artifacts
  // does not implement the /<package>/<version> metadata route Corepack calls.
  // Seed the same cache shape Corepack writes, using npm pack because npm can
  // resolve Yarn through the Azure Artifacts pull-through feed.
  run('tar', ['-xzf', tarballPath, '-C', stagingDirectory, '--strip-components=1'], temporaryDirectory);

  writeFileSync(join(stagingDirectory, '.corepack'), JSON.stringify({
    locator: {
      name: 'yarn',
      reference: yarnVersion
    },
    bin: {
      yarn: './bin/yarn.js',
      yarnpkg: './bin/yarn.js'
    },
    hash: `sha1.${packEntry.shasum}`
  }));

  try {
    renameSync(stagingDirectory, installDirectory);
    cacheSeeded = true;
  } catch (error) {
    // Lost a race with a concurrent build (same worktree, same COREPACK_HOME).
    // The winner's renameSync atomically populated installDirectory; ours then
    // fails because the destination already exists. Filesystem-level error
    // codes vary:
    //   - Windows / macOS HFS+: EEXIST when the destination dir exists.
    //   - Linux / macOS APFS:   ENOTEMPTY (rename(2) rejects renaming over a
    //                           non-empty directory; see
    //                           https://man7.org/linux/man-pages/man2/rename.2.html).
    // Both mean the cache is already in place, so log and exit cleanly. Any
    // other error code is a real failure (permission, ENOSPC, etc.) and is
    // re-thrown.
    if (error?.code === 'EEXIST' || error?.code === 'ENOTEMPTY') {
      console.log(`Corepack cache already contains yarn@${yarnVersion} at ${installDirectory}`);
    } else {
      throw error;
    }
  }

  if (cacheSeeded) {
    console.log(`Seeded Corepack cache with yarn@${yarnVersion} at ${installDirectory}`);
  }
} finally {
  rmSync(temporaryDirectory, { recursive: true, force: true });
  rmSync(stagingDirectory, { recursive: true, force: true });
}

function getCorepackHome() {
  if (process.env.COREPACK_HOME) {
    return process.env.COREPACK_HOME;
  }

  // build.sh / build.ps1 / the GitHub Actions workflow / the AzDO pipelines all
  // set COREPACK_HOME explicitly to a build-scoped directory. This fallback
  // exists only for ad-hoc invocations of this script (e.g. local debugging,
  // running `node ./scripts/prepareCorepackYarn.mjs` directly). It mirrors
  // Corepack 0.34.x's own cache-path resolution so we seed the directory
  // Corepack will later read from.
  // Source: https://github.com/nodejs/corepack/blob/v0.34.7/sources/folderUtils.ts
  const baseDirectory = process.env.XDG_CACHE_HOME
    ?? process.env.LOCALAPPDATA
    ?? join(homedir(), process.platform === 'win32' ? 'AppData/Local' : '.cache');

  return join(baseDirectory, 'node', 'corepack');
}

function getNpmInvocation() {
  if (process.platform !== 'win32') {
    return { command: 'npm', args: [] };
  }

  const npmCliPath = join(dirname(process.execPath), 'node_modules', 'npm', 'bin', 'npm-cli.js');

  if (!existsSync(npmCliPath)) {
    fail(`Could not find npm CLI at ${npmCliPath}. Corepack Yarn cache seeding requires the npm CLI that ships with Node.js.`);
  }

  // Avoid spawning npm.cmd directly on Windows. Some hosted images reject the
  // .cmd shim from child_process.spawnSync with EINVAL, while invoking the
  // npm CLI through node.exe avoids shell/cmd parsing entirely.
  return { command: process.execPath, args: [npmCliPath] };
}

function parseNpmPackJson(stdout) {
  const jsonStart = stdout.indexOf('[');

  if (jsonStart === -1) {
    fail(`npm pack did not emit JSON output. Output: ${stdout}`);
  }

  return JSON.parse(stdout.slice(jsonStart));
}

function run(command, args, cwd) {
  const result = spawnSync(command, args, {
    cwd,
    encoding: 'utf8',
    stdio: ['ignore', 'pipe', 'pipe']
  });

  if (result.status !== 0) {
    if (result.stdout) {
      process.stderr.write(result.stdout);
    }

    if (result.stderr) {
      process.stderr.write(result.stderr);
    }

    const errorDetails = result.error ? ` (${result.error.message})` : '';
    fail(`${command} ${args.join(' ')} failed with exit code ${result.status ?? 'unknown'}${errorDetails}.`);
  }

  if (result.stderr) {
    process.stderr.write(result.stderr);
  }

  return result;
}

function fail(message) {
  console.error(message);
  process.exit(1);
}
