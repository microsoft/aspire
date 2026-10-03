// Deterministic helpers for the auto-sec agentic workflow (.github/workflows/auto-sec.md).
//
// Two consumers:
//   1. The `approve-dependabot-pr` safe-output job calls `runApprovalJob` through
//      actions/github-script. The agent only *requests* an approval; every gate is
//      re-verified here against live GitHub and registry data before the Aspire bot
//      App submits an APPROVE review, so a confused or prompt-injected agent cannot
//      approve anything the gates reject.
//   2. The agent runs `node auto-sec.js lookup <ecosystem> <name> <version>` to get the
//      publish date (7-day cooldown) and approved-feed availability of a candidate
//      version before proposing it in an auto-sec pull request.

'use strict';

const COOLDOWN_DAYS = 7;
// The single long-lived branch behind the open auto-sec PR (see auto-sec.md safe-outputs).
const AUTO_SEC_BRANCH = 'auto-sec/security-updates';
// Dependabot alerts describe the default branch, so only PRs into it can fix them.
const BASE_BRANCH = 'main';
const DEPENDABOT_LOGIN = 'dependabot[bot]';
const DEFAULT_BOT_LOGIN = 'aspire-repo-bot[bot]';
// At most this many reviews are submitted per run. Requests beyond the limit are still
// evaluated (bounded by MAX_EVALUATED_REQUESTS) so ineligible or already-approved PRs
// at the front of the agent's list cannot starve eligible ones on every run.
const MAX_APPROVALS = 10;
const MAX_EVALUATED_REQUESTS = 100;
// Upper bound on distinct package versions one PR may introduce. Each needs a registry
// lookup for the cooldown gate, so a larger diff is left to a human reviewer.
const MAX_VERSION_CHANGES = 50;

// Package sources that are always acceptable in a lockfile or manifest: the
// repository's dnceng public Azure Artifacts feeds only. Any other source must
// already be present in the file on the PR base, so a bump can never move a
// dependency to a new registry or feed. Sources are compared as keys built by
// `sourceKey`, never as bare hosts, because pkgs.dev.azure.com hosts every
// Azure DevOps organization's feeds.
const APPROVED_SOURCE_PREFIXES = [
    'https://pkgs.dev.azure.com/dnceng/public/_packaging/',
    'https://dnceng.pkgs.visualstudio.com/public/_packaging/',
];

const APPROVED_NPM_REGISTRY = 'https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public-npm/npm/registry/';

// Dependency manifests a Dependabot PR may touch and still be auto-approved. Anything
// else (feed configuration, SDK pins, workflow files, source code) disqualifies the PR.
const ALLOWED_MANIFEST_BASENAMES = new Set([
    'package.json',
    'package-lock.json',
    'npm-shrinkwrap.json',
    'yarn.lock',
    'pnpm-lock.yaml',
    'uv.lock',
    'pyproject.toml',
    'directory.packages.props',
]);

// Manifests that can carry executable content (package.json scripts, pyproject.toml
// build hooks, MSBuild targets and properties). Lockfiles are data only and are
// covered by the package-source gate instead.
const VERSION_ONLY_MANIFEST_BASENAMES = new Set([
    'package.json',
    'pyproject.toml',
    'directory.packages.props',
]);

// Characters that can appear inside a version token, such as `4.17.21`,
// `1.0.0-rc.1+build.5`, or `v2.0.0`.
const VERSION_TOKEN_CHAR = /[0-9A-Za-z.+-]/;
const VERSION_TOKEN = /^v?\d+(?:\.\d+)*(?:[-+][0-9A-Za-z.+-]*)?$/;

// package.json maps whose string values are dependency version ranges. A version-only
// edit of package.json may change nothing outside these maps.
const PACKAGE_JSON_DEPENDENCY_MAPS = new Set([
    'dependencies',
    'devDependencies',
    'optionalDependencies',
    'peerDependencies',
    'overrides',
    'resolutions',
]);

// One npm range comparator (https://docs.npmjs.com/cli/v10/using-npm/semver#ranges):
//   4.17.21  ^4.17.21  ~1.2  >=1.0.0  <2  1.x  *  -  ||
const NPM_RANGE_PART = /^(?:\|\||-|\*|[xX]|(?:[\^~]|[<>]=?|=)?v?\d+(?:\.(?:\d+|[xX*]))*(?:[-+][0-9A-Za-z.+-]*)?)$/;

// A package.json entry on its own line whose value is a version range, optionally behind an
// npm alias (`"lodash": "npm:lodash@^4.17.21"`). Script commands such as
// `"build": "vite --mode=1"` do not match.
const PACKAGE_JSON_VERSION_LINE = /^\s*"(?:[^"\\]|\\.)+"\s*:\s*"(?:npm:(?:@[^@"\s/]+\/)?[^@"\s]+@)?([^"]*)"\s*,?\s*$/d;

// A quoted PEP 508 requirement string on its own line in a TOML array, with an optional
// extras list and environment marker (https://peps.python.org/pep-0508/):
//   "requests>=2.32.3",   'jinja2[i18n] ~= 3.1.6, < 4; python_version >= "3.9"',
const PYPROJECT_VERSION_LINE = /^\s*(["'])[A-Za-z0-9][A-Za-z0-9._-]*\s*(?:\[[A-Za-z0-9._,\s-]*\])?\s*((?:===|==|~=|>=|<=|!=|>|<)\s*[0-9A-Za-z.*+!-]+(?:\s*,\s*(?:===|==|~=|>=|<=|!=|>|<)\s*[0-9A-Za-z.*+!-]+)*)\s*(?:;(?:(?!\1)[^\n])*)?\1\s*,?\s*$/d;

// A single central package version element on its own line:
//   <PackageVersion Include="System.Text.Json" Version="9.0.5" />
//   <PackageVersion Update="Npgsql.EntityFrameworkCore.PostgreSQL" Version="[9.0.4]" />
const PACKAGES_PROPS_VERSION_LINE = /^\s*<PackageVersion\s+(?:Include|Update)="[^"$%@]+"\s+Version="(\[?[0-9A-Za-z.+-]+\]?)"\s*\/>\s*$/d;

const DEFAULT_PORTS = {
    'http': 80,
    'https': 443,
    'git+http': 80,
    'git+https': 443,
    'ssh': 22,
    'git+ssh': 22,
    'git': 9418,
};

const SUCCESSFUL_CHECK_CONCLUSIONS = new Set(['success', 'skipped', 'neutral']);

// Dependabot branch names look like `dependabot/<package-manager>/<dir>/<pkg>-<version>`.
// Map the package-manager segment to the Dependabot alert ecosystem name.
const BRANCH_ECOSYSTEMS = {
    npm_and_yarn: 'npm',
    pip: 'pip',
    uv: 'pip',
    nuget: 'nuget',
    github_actions: 'actions',
};

function parseVersion(value) {
    if (typeof value !== 'string') {
        return null;
    }

    // Accept `1.2.3`, `v1.2.3`, `1.2`, `1.2.3.4`, `1.2.3-beta.1`, `1.2.3+build`.
    const match = /^v?(\d+)(?:\.(\d+))?(?:\.(\d+))?(?:\.(\d+))?(?:-([0-9A-Za-z.-]+))?(?:\+[0-9A-Za-z.-]+)?$/.exec(value.trim());
    if (!match) {
        return null;
    }

    return {
        // BigInt keeps components above Number.MAX_SAFE_INTEGER distinct.
        parts: [match[1], match[2], match[3], match[4]].map(part => (part === undefined ? 0n : BigInt(part))),
        prerelease: match[5] ?? '',
    };
}

function compareVersions(left, right) {
    const a = parseVersion(left);
    const b = parseVersion(right);
    if (!a || !b) {
        return null;
    }

    for (let i = 0; i < a.parts.length; i++) {
        if (a.parts[i] !== b.parts[i]) {
            return a.parts[i] < b.parts[i] ? -1 : 1;
        }
    }

    // A prerelease sorts before its release (1.0.0-rc.1 < 1.0.0).
    if (a.prerelease === b.prerelease) {
        return 0;
    }
    if (a.prerelease === '') {
        return 1;
    }
    if (b.prerelease === '') {
        return -1;
    }
    return comparePrerelease(a.prerelease, b.prerelease);
}

// SemVer 2.0.0 section 11 (https://semver.org/#spec-item-11): compare dot-separated
// identifiers left to right. Numeric identifiers compare numerically and sort before
// alphanumeric ones, alphanumeric identifiers compare in ASCII order, and a shorter
// identifier list sorts first when all preceding identifiers are equal
// (alpha < alpha.1 < alpha.beta < beta < beta.2 < beta.11 < rc.1).
function comparePrerelease(left, right) {
    const a = left.split('.');
    const b = right.split('.');
    for (let i = 0; i < Math.min(a.length, b.length); i++) {
        const aNumeric = /^\d+$/.test(a[i]);
        const bNumeric = /^\d+$/.test(b[i]);
        if (aNumeric && bNumeric) {
            const aValue = BigInt(a[i]);
            const bValue = BigInt(b[i]);
            if (aValue !== bValue) {
                return aValue < bValue ? -1 : 1;
            }
        } else if (aNumeric !== bNumeric) {
            return aNumeric ? -1 : 1;
        } else if (a[i] !== b[i]) {
            return a[i] < b[i] ? -1 : 1;
        }
    }
    return a.length === b.length ? 0 : (a.length < b.length ? -1 : 1);
}

// A change to the major version is breaking, and for 0.x a change to the minor version
// is too (0.3.x -> 0.4.x). Patch updates within the same 0.x minor (0.0.3 -> 0.0.4)
// are accepted, matching the policy documented in auto-sec.md and the README.
// Unparseable versions and downgrades fail closed and are treated as breaking.
function isBreakingChange(from, to) {
    const a = parseVersion(from);
    const b = parseVersion(to);
    if (!a || !b) {
        return true;
    }

    const [aMajor, aMinor] = a.parts;
    const [bMajor, bMinor] = b.parts;
    if (aMajor !== bMajor) {
        return true;
    }
    if (aMajor === 0n && aMinor !== bMinor) {
        return true;
    }
    return compareVersions(from, to) !== -1;
}

function normalizePackageName(ecosystem, name) {
    const lowered = String(name ?? '').trim().toLowerCase();
    // PEP 503: runs of `-`, `_` and `.` are equivalent in Python package names.
    return ecosystem === 'pip' ? lowered.replace(/[-_.]+/g, '-') : lowered;
}

function ecosystemFromBranch(headRef) {
    const match = /^dependabot\/([^/]+)\//.exec(headRef ?? '');
    return match ? BRANCH_ECOSYSTEMS[match[1]] ?? null : null;
}

function stripTrailingPunctuation(value) {
    return value.replace(/[.,;:)]+$/, '');
}

// Extract `{ name, from, to }` updates from a Dependabot PR. Dependabot writes:
//   title: "Bump lodash from 4.17.20 to 4.17.21 in /playground/app"
//   body (single):  "Bumps [lodash](https://github.com/lodash/lodash) from 4.17.20 to 4.17.21."
//   body (grouped): "Updates `lodash` from 4.17.20 to 4.17.21"
// Grouped titles ("Bump the npm_and_yarn group across 2 directories with 3 updates")
// carry no versions, so the body is authoritative and the title is a fallback.
// Updates are keyed by the full transition: a grouped PR can move the same package to
// the same version from different starting versions in different directories
// (2.0.0 -> 2.0.1 and 1.9.0 -> 2.0.1), and each transition is checked separately.
function parseDependabotUpdates(title, body) {
    const updates = new Map();
    const add = (name, from, to) => {
        const cleanFrom = stripTrailingPunctuation(from);
        const cleanTo = stripTrailingPunctuation(to);
        const key = `${name.toLowerCase()}@${cleanFrom}->${cleanTo}`;
        if (!updates.has(key)) {
            updates.set(key, { name, from: cleanFrom, to: cleanTo });
        }
    };

    for (const match of String(body ?? '').matchAll(/Updates `([^`]+)` from (\S+) to (\S+)/g)) {
        add(match[1], match[2], match[3]);
    }
    for (const match of String(body ?? '').matchAll(/Bumps \[([^\]]+)\]\([^)]*\) from (\S+) to (\S+)/g)) {
        add(match[1], match[2], match[3]);
    }
    if (updates.size === 0) {
        const titleMatch = /^Bump (\S+) from (\S+) to (\S+)/.exec(String(title ?? ''));
        if (titleMatch) {
            add(titleMatch[1], titleMatch[2], titleMatch[3]);
        }
    }

    return [...updates.values()];
}

function basenameOf(path) {
    const index = String(path).lastIndexOf('/');
    return index < 0 ? path : path.slice(index + 1);
}

function isAllowedManifest(path) {
    if (String(path).startsWith('.github/')) {
        return false;
    }
    return ALLOWED_MANIFEST_BASENAMES.has(basenameOf(path).toLowerCase());
}

// Reduce a package URL to the identity of the source that serves it, so two URLs from
// the same feed compare equal while feeds on a shared multi-tenant host do not:
//   https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public-npm/npm/registry/lodash/-/lodash-4.17.21.tgz
//     -> https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public-npm/
//   https://pkgs.dev.azure.com/contoso/_packaging/feed/npm/registry/lodash
//     -> https://pkgs.dev.azure.com/contoso/_packaging/feed/
//   https://pkgs.dev.azure.com/contoso/project/_apis/...   -> https://pkgs.dev.azure.com/contoso/project/
//   https://registry.npmjs.org/lodash/-/lodash-4.17.21.tgz -> https://registry.npmjs.org/
// Azure Artifacts feeds are keyed through `/_packaging/<feed>/`; any other URL on an Azure
// DevOps host is keyed by organization and project; everything else is keyed by host.
// A non-default port is part of the origin (`https://registry.npmjs.org:8443/` is a
// different server than `https://registry.npmjs.org/`); the scheme's default port is
// dropped so `:443` on an https URL compares equal to no port.
function sourceKey(scheme, host, port, path) {
    const loweredScheme = scheme.toLowerCase();
    const portSuffix = port && DEFAULT_PORTS[loweredScheme] !== Number(port) ? `:${Number(port)}` : '';
    const origin = `${loweredScheme}://${host.toLowerCase()}${portSuffix}`;
    const feed = /^(\/(?:[^/?#]+\/){0,2}_packaging\/[^/?#]+\/)/i.exec(path);
    if (feed) {
        return `${origin}${feed[1].toLowerCase()}`;
    }
    if (host.toLowerCase() === 'pkgs.dev.azure.com' || /\.pkgs\.visualstudio\.com$/i.test(host)) {
        const scope = /^(\/(?:[^/?#]+\/){0,2})/.exec(`${path}/`);
        return `${origin}${scope[1].toLowerCase()}`;
    }
    return `${origin}/`;
}

// Every URL scheme npm, yarn, pnpm, uv and NuGet accept for a package location:
//   https://registry.npmjs.org/a/-/a-1.0.0.tgz   git+https://github.com/o/r.git#v1
//   git+ssh://git@github.com/o/r.git             git://github.com/o/r.git
//   ssh://git@github.com/o/r.git
// URI schemes and hosts are case-insensitive (RFC 3986 sections 3.1 and 3.2.2), so
// `HTTPS://Registry.Example.com/` is matched and keyed like its lowercase form.
// Local delivery protocols are sources too, keyed by their target path:
//   "x": "file:../x.tgz"   "x@link:../x"   "x": "portal:../x"   file:///tmp/x.tgz
// JSON and TOML string escapes are decoded first, because the package manager reads
// `https:\/\/evil.example\/x.tgz` or `https\u003a//evil.example/x.tgz` as a plain URL
// (https://www.rfc-editor.org/rfc/rfc8259#section-7, https://toml.io/en/v1.0.0#string).
function extractSources(text) {
    const sources = new Set();
    const decoded = String(text ?? '')
        .replace(/\\u([0-9A-Fa-f]{4})/g, (_, hex) => String.fromCharCode(parseInt(hex, 16)))
        .replace(/\\U([0-9A-Fa-f]{8})/g, (_, hex) => String.fromCodePoint(Math.min(parseInt(hex, 16), 0x10FFFF)))
        .replace(/\\\//g, '/');
    // Each candidate runs through the WHATWG URL parser (https://url.spec.whatwg.org/), the
    // same parser Node and npm use, so userinfo cannot hide the destination:
    //   https://user@registry.npmjs.org@evil.example/x.tgz   -> host evil.example
    //   https://evil.example\@registry.npmjs.org/x.tgz       -> host evil.example
    // http(s) transports parse as special URLs (`\` ends the authority); ssh and git parse
    // as non-special URLs so their explicit ports are kept. A candidate the parser rejects
    // is keyed by its raw text, which is never an approved source.
    for (const match of decoded.matchAll(/\b(https?|git\+https?|git\+ssh|git|ssh):\/\/[^\s"'<>]*/gi)) {
        const scheme = match[1].toLowerCase();
        const transport = scheme.replace(/^git\+/, '');
        const parseScheme = transport === 'http' || transport === 'https' ? transport : 'x-auto-sec';
        let url;
        try {
            url = new URL(`${parseScheme}${match[0].slice(match[1].length)}`);
        } catch {
            sources.add(`unparsed:${match[0]}`);
            continue;
        }
        sources.add(sourceKey(scheme, url.hostname, url.port, url.pathname || '/'));
    }
    for (const match of decoded.matchAll(/(?<![A-Za-z0-9+.-])(file|link|portal):([^\s"'<>,;)]*)/gi)) {
        sources.add(`${match[1].toLowerCase()}:${match[2]}`);
    }
    return sources;
}

function isApprovedSource(source) {
    return APPROVED_SOURCE_PREFIXES.some(prefix => source.startsWith(prefix));
}

// Package metadata that names project pages rather than package delivery locations:
//   "funding": "https://github.com/sponsors/x"
//   "funding": { "type": "github", "url": "https://github.com/sponsors/x" }
//   "repository": {                      <- multi-line objects and arrays are dropped
//     "url": "git+https://github.com/o/r.git"     through their closing bracket
//   }
//   [project.urls]                       <- pyproject.toml metadata table
//   Homepage = "https://example.com"
const METADATA_JSON_KEYS = '(?:funding|repository|homepage|bugs|author|contributors|maintainers)';
const METADATA_JSON_INLINE = new RegExp(`"${METADATA_JSON_KEYS}"\\s*:\\s*(?:"(?:[^"\\\\]|\\\\.)*"|\\{[^{}]*\\}|\\[(?:[^\\[\\]{}]|\\{[^{}]*\\})*\\])\\s*,?`, 'g');
const METADATA_JSON_OPEN = new RegExp(`"${METADATA_JSON_KEYS}"\\s*:\\s*[\\[{]\\s*$`);
const METADATA_LOCKFILE_BASENAMES = new Set(['package-lock.json', 'npm-shrinkwrap.json']);
const PACKAGE_JSON_METADATA_KEYS = new Set(['funding', 'repository', 'homepage', 'bugs', 'author', 'contributors', 'maintainers']);
// A v1 package-lock.json nests packages by name under `dependencies`, so a package can be
// named like a metadata key. Its entry carries package fields that metadata never has:
//   "dependencies": {
//     "bugs": { "version": "1.0.0", "resolved": "https://registry.npmjs.org/bugs/-/bugs-1.0.0.tgz" }
//   }
// A metadata-keyed value containing any of these keys is a package and is never dropped.
const PACKAGE_ENTRY_KEY = /"(?:version|resolved|integrity|requires|dependencies|dev|optional|bundled|link)"\s*:/;

// Removes metadata URLs from `text`. A line equal to `segmentBreak` resets the scanner, so
// separately added runs of patch lines are never read as one contiguous JSON fragment.
// A multi-line metadata value is buffered until it closes; one cut off by a segment break or
// the end of the text is kept when `keepUnclosed` is set (the head side, where keeping is
// strict) and dropped otherwise (the base side, where dropping is strict).
function withoutMetadataUrls(text, segmentBreak = null, keepUnclosed = false) {
    const out = [];
    let depth = 0;
    let tomlMetadata = false;
    let pending = null;
    const settle = keep => {
        if (pending) {
            out.push(...(keep ? pending.lines : [pending.prefix]));
            pending = null;
        }
    };
    for (const line of String(text ?? '').split('\n')) {
        if (line === segmentBreak) {
            settle(keepUnclosed);
            depth = 0;
            tomlMetadata = false;
            continue;
        }
        if (depth > 0) {
            // Count brackets outside string literals so a URL containing `}` cannot end
            // (or extend) the metadata object early.
            const structural = line.replace(/"(?:[^"\\]|\\.)*"/g, '');
            depth += (structural.match(/[[{]/g) ?? []).length - (structural.match(/[\]}]/g) ?? []).length;
            depth = Math.max(depth, 0);
            pending.lines.push(line);
            if (depth === 0) {
                settle(pending.lines.some(text => PACKAGE_ENTRY_KEY.test(text)));
            }
            continue;
        }
        const tomlHeader = /^\s*\[\[?\s*([A-Za-z0-9_.-]+)\s*\]\]?\s*$/.exec(line);
        if (tomlHeader) {
            tomlMetadata = /^(?:project|tool\.poetry)\.urls$/i.test(tomlHeader[1]);
            out.push(line);
            continue;
        }
        if (tomlMetadata) {
            continue;
        }
        const stripped = line.replace(METADATA_JSON_INLINE, value => PACKAGE_ENTRY_KEY.test(value) ? value : '');
        const open = METADATA_JSON_OPEN.exec(stripped);
        if (open) {
            pending = { prefix: stripped.slice(0, open.index), lines: [line] };
            depth = 1;
            continue;
        }
        out.push(stripped);
    }
    settle(keepUnclosed);
    return out.join('\n');
}

// Sources present in the head version of a file that are neither approved nor already
// present on the base version. A non-empty result means the PR introduces a new source.
// Metadata URLs on the base never authorize a head source: an existing
// `https://github.com/sponsors/...` funding link must not admit a `resolved` tarball from
// github.com. Metadata is ignored on the head side only where it is unambiguous: in npm
// lockfiles, where each package's `resolved` field is the fetch location and is still
// checked, and in a parseable package.json, where only the top-level metadata fields are
// dropped so a dependency named `bugs` with a URL value still counts. Elsewhere a
// metadata-looking key may be a dependency name whose value is a source, so it counts.
function findNewSources(baseText, headText, path = '', segmentBreak = null) {
    const name = basenameOf(path).toLowerCase();
    const packageJson = text => name === 'package.json' ? packageJsonWithoutMetadata(text) : null;
    const baseSources = extractSources(packageJson(baseText) ?? withoutMetadataUrls(baseText, segmentBreak));
    const headSources = extractSources(packageJson(headText)
        ?? (METADATA_LOCKFILE_BASENAMES.has(name) ? withoutMetadataUrls(headText, segmentBreak, true) : headText));
    return [...headSources]
        .filter(source => !baseSources.has(source) && !isApprovedSource(source))
        .sort();
}

// A full package.json document re-serialized without its top-level metadata fields, or
// null when `text` is not a JSON object (for example, a patch-gate fragment).
function packageJsonWithoutMetadata(text) {
    const parsed = readJson(String(text ?? ''));
    if (parsed === null || typeof parsed !== 'object' || Array.isArray(parsed)) {
        return null;
    }
    return JSON.stringify(Object.fromEntries(Object.entries(parsed).filter(([key]) => !PACKAGE_JSON_METADATA_KEYS.has(key))), null, 1);
}

function isCooldownSatisfied(publishedAt, now, days = COOLDOWN_DAYS) {
    const published = Date.parse(publishedAt ?? '');
    if (Number.isNaN(published)) {
        return false;
    }
    return now.getTime() - published >= days * 24 * 60 * 60 * 1000;
}

// Split an npm package spec into name and version range at the `@` that follows the name.
// The leading `@` of a scoped name is part of the name:
//   lodash@^4.17.20          -> lodash, ^4.17.20
//   @babel/parser@7.29.3     -> @babel/parser, 7.29.3
//   lodash@npm:^4.17.20      -> lodash, npm:^4.17.20 (yarn berry)
function splitNpmSpec(spec) {
    const index = spec.indexOf('@', 1);
    return index < 0 ? { name: spec, range: '' } : { name: spec.slice(0, index), range: spec.slice(index + 1) };
}

// Reduce a range to its version: `^4.17.21`, `~4.17.21`, `>=4.17.21`, `=4.17.21` and
// `v4.17.21` all become `4.17.21`. Compound ranges (`>=1 <2`) keep only the first bound.
function stripRangeOperators(range) {
    return String(range ?? '').trim().replace(/^npm:/, '').replace(/^[\^~=<>v\s]+/, '').split(/[\s,|]/)[0];
}

// Name targeted by an `overrides` or `resolutions` key. Keys can be a bare name, a spec
// with a range, or a glob path ending in the package:
//   lodash, lodash@^4, **/lodash, parent/lodash, **/@scope/pkg, @scope/pkg@1
function overrideKeyName(key) {
    const segments = key.split('/');
    const last = segments.length >= 2 && segments[segments.length - 2].startsWith('@')
        ? segments.slice(-2).join('/')
        : segments[segments.length - 1];
    return splitNpmSpec(last).name;
}

function readJson(text) {
    try {
        return JSON.parse(text);
    } catch {
        return null;
    }
}

// Every manifest reader below returns the `{ name, version, key }` entries the file
// declares or resolves. A package can appear more than once (a lockfile with a top-level
// and a nested copy, or a dependency plus an override), and every occurrence is returned.
// `key` identifies the occurrence (an install path, a yarn selector, a pnpm dependency
// reference) when the format has one, so a consumer moving between two versions that
// both stay in the file is still seen as a version change.

// package.json pins a range per dependency map; `overrides` nest by package name and
// `resolutions` keys can be glob paths (`**/lodash`), so both are searched recursively.
// A nested override object pins its own package through the `.` key:
//   "overrides": { "parent": { ".": "1.0.0", "lodash": "4.17.21" } }
function packageJsonEntries(text) {
    const json = readJson(text);
    if (!json) {
        return [];
    }
    const entries = [];
    for (const map of ['dependencies', 'devDependencies', 'optionalDependencies', 'peerDependencies']) {
        for (const [key, value] of Object.entries(json[map] ?? {})) {
            if (typeof value === 'string') {
                entries.push({ name: key, version: stripRangeOperators(value), key: `${map}/${key}` });
            }
        }
    }
    const visit = node => {
        for (const [key, value] of Object.entries(node ?? {})) {
            if (typeof value === 'string' && key !== '.') {
                entries.push({ name: overrideKeyName(key), version: stripRangeOperators(value) });
            } else if (value && typeof value === 'object') {
                if (typeof value['.'] === 'string') {
                    entries.push({ name: overrideKeyName(key), version: stripRangeOperators(value['.']) });
                }
                visit(value);
            }
        }
    };
    visit(json.overrides);
    visit(json.resolutions);
    return entries;
}

// package-lock.json / npm-shrinkwrap.json:
//   v2/v3: { "packages": { "node_modules/a/node_modules/lodash": { "version": "4.17.21" } } }
//   v1:    { "dependencies": { "lodash": { "version": "4.17.21", "dependencies": { ... } } } }
function packageLockEntries(text) {
    const json = readJson(text);
    if (!json) {
        return [];
    }
    const entries = [];
    for (const [path, entry] of Object.entries(json.packages ?? {})) {
        const index = path.lastIndexOf('node_modules/');
        if (index >= 0 && typeof entry?.version === 'string') {
            entries.push({ name: path.slice(index + 'node_modules/'.length), version: entry.version, key: path });
        }
    }
    const visit = (dependencies, parent) => {
        for (const [key, entry] of Object.entries(dependencies ?? {})) {
            const path = `${parent}/${key}`;
            if (typeof entry?.version === 'string') {
                entries.push({ name: key, version: entry.version, key: `v1${path}` });
            }
            visit(entry?.dependencies, path);
        }
    };
    visit(json.dependencies, '');
    return entries;
}

// yarn.lock blocks start with an unindented header listing every spec the entry
// resolves, followed by two-space indented fields:
//   classic: "lodash@^4.17.20", lodash@^4.17.21:\n  version "4.17.21"
//   berry:   "lodash@npm:^4.17.20":\n  version: 4.17.21
// Nested `dependencies:` entries are indented further and never bind a version here.
// A header can list several selectors, including aliases of other names; one entry is
// returned per selector, keyed by the selector, so moving `foo@^1.5.0` from the 1.x
// block into an existing 2.x block is visible.
function yarnLockEntries(text) {
    const entries = [];
    let specs = [];
    for (const line of String(text ?? '').split(/\r?\n/)) {
        if (/^\S/.test(line)) {
            specs = !line.startsWith('#') && line.trimEnd().endsWith(':')
                ? [...new Set(line.trimEnd().slice(0, -1).split(',')
                    .map(spec => spec.trim().replace(/^"|"$/g, ''))
                    .filter(spec => splitNpmSpec(spec).name))]
                : [];
            continue;
        }
        const version = specs.length ? /^ {2}version:?\s+"?([^"\s]+)"?\s*$/.exec(line) : null;
        if (version) {
            entries.push(...specs.map(spec => ({ name: splitNpmSpec(spec).name, version: version[1], key: spec })));
        }
    }
    return entries;
}

// pnpm-lock.yaml lists each resolved package as a two-space indented key, with optional
// quotes, an optional leading `/`, and an optional peer suffix:
//   v9:  '@babel/parser@7.29.3':     lodash@4.17.21(react@18.0.0):
//   v6:  /lodash@4.17.21:
//   v5:  /lodash/4.17.21:            /@babel/parser/7.29.3:
// Keys without a version (`importers:` children such as `  .:`) are skipped.
// Each package key appears once per version, so the dependency references that select a
// version are returned too, keyed by the importer or package that holds them:
//   importers:\n  .:\n    dependencies:\n      lodash:\n        specifier: ^4\n        version: 4.17.21
//   snapshots:\n  a@1.0.0:\n    dependencies:\n      lodash: 4.17.21(peer@1.0.0)
// References to non-registry targets (`link:../x`) carry no version and are skipped.
function pnpmLockEntries(text) {
    const entries = [];
    for (const [, rawKey] of String(text ?? '').matchAll(/^ {2}(\S.*?):\s*$/gm)) {
        const key = rawKey.replace(/^'|'$/g, '').replace(/\(.*$/, '');
        const trimmed = key.replace(/^\//, '');
        let { name, range } = splitNpmSpec(trimmed);
        if (!range && key.startsWith('/')) {
            const slash = trimmed.lastIndexOf('/');
            name = trimmed.slice(0, slash);
            range = trimmed.slice(slash + 1);
        }
        if (range && name) {
            entries.push({ name, version: range });
        }
    }
    const unquote = value => value.trim().replace(/^'|'$/g, '');
    let section = '';
    let owner = '';
    let group = '';
    let pending = '';
    for (const line of String(text ?? '').split(/\r?\n/)) {
        const reference = (name, value) => {
            const version = unquote(value).replace(/\(.*$/, '');
            if (name && /^\d/.test(version)) {
                entries.push({ name, version, key: `${section}/${owner}/${group}/${name}` });
            }
        };
        let match;
        if ((match = /^(\S.*?):\s*$/.exec(line))) {
            [section, owner, group, pending] = [match[1], '', '', ''];
        } else if ((match = /^ {2}(\S.*?):\s*$/.exec(line))) {
            [owner, group, pending] = [unquote(match[1]), '', ''];
        } else if ((match = /^ {4}(\w*[dD]ependencies):\s*$/.exec(line))) {
            [group, pending] = [match[1], ''];
        } else if (group && (match = /^ {6}(\S.*?):\s*(\S.*)?$/.exec(line))) {
            pending = '';
            if (match[2]) {
                reference(unquote(match[1]), match[2]);
            } else {
                pending = unquote(match[1]);
            }
        } else if (pending && (match = /^ {8}version:\s*(\S.*)$/.exec(line))) {
            reference(pending, match[1]);
            pending = '';
        } else if (!/^ {8}/.test(line)) {
            [group, pending] = /^ {4}/.test(line) ? ['', ''] : [group, pending];
        }
    }
    return entries;
}

// uv.lock: [[package]]\nname = "jinja2"\nversion = "3.1.6"
function uvLockEntries(text) {
    const entries = [];
    for (const block of String(text ?? '').split(/^\[\[package\]\]\s*$/m).slice(1)) {
        const body = block.split(/^\[/m)[0];
        const name = /^name\s*=\s*"([^"]+)"/m.exec(body);
        const version = /^version\s*=\s*"([^"]+)"/m.exec(body);
        if (name && version) {
            entries.push({ name: name[1], version: version[1] });
        }
    }
    return entries;
}

// Real package name behind an npm alias selector, or the name itself:
//   lodash, ^4                      -> lodash
//   my-lodash, npm:lodash@^4        -> lodash
//   lodash, npm:^4 (yarn berry)     -> lodash
function npmRealName(name, range) {
    const target = String(range ?? '').startsWith('npm:') ? splitNpmSpec(range.slice(4)) : null;
    return target?.range ? target.name : name;
}

// An npm registry tarball path names its package and version:
//   <registry>/lodash/-/lodash-4.17.21.tgz
//   <registry>/@babel/parser/-/parser-7.29.3.tgz   (the scope slash may be %2f)
function isNpmTarballFor(url, names, version) {
    let pathname;
    try {
        pathname = decodeURIComponent(new URL(url).pathname);
    } catch {
        return false;
    }
    return [...names].some(name => pathname.endsWith(`/${name}/-/${name.split('/').pop()}-${version}.tgz`));
}

// PyPI distribution filenames (https://packaging.python.org/en/latest/specifications/binary-distribution-format/,
// https://packaging.python.org/en/latest/specifications/source-distribution-format/):
//   jinja2-3.1.6-py3-none-any.whl     Jinja2-3.1.6.tar.gz     python-dateutil-2.8.2.tar.gz
// Names compare after PEP 503 normalization; the version must match exactly.
function isPythonDistributionFor(url, name, version) {
    let file;
    try {
        file = decodeURIComponent(new URL(url).pathname.split('/').pop());
    } catch {
        return false;
    }
    const normalize = value => value.toLowerCase().replace(/[-_.]+/g, '-');
    let distName;
    let distVersion;
    if (file.endsWith('.whl')) {
        [distName, distVersion] = file.slice(0, -4).split('-');
    } else {
        const stem = file.replace(/\.(?:tar\.gz|tar\.bz2|tgz|zip)$/, '');
        const dash = stem.lastIndexOf('-');
        if (stem === file || dash < 0) {
            return false;
        }
        [distName, distVersion] = [stem.slice(0, dash), stem.slice(dash + 1)];
    }
    return normalize(distName ?? '') === normalize(name) && distVersion === version;
}

// Each artifact a lockfile downloads, with the identity the entry claims for it. `bound`
// is true only when the artifact itself names that package and version, so a lockfile
// cannot keep `lodash@4.17.21` while pointing it at another package's tarball on the same
// registry. Install-time integrity checks then verify the bytes against that artifact.
//   package-lock.json:  "node_modules/lodash": { "version": "4.17.21", "resolved": "<url>" }
//   yarn.lock classic:  lodash@^4:\n  version "4.17.21"\n  resolved "<url>#sha1"
//   yarn.lock berry:    "lodash@npm:^4":\n  version: 4.17.21\n  resolution: "lodash@npm:4.17.21"
//   pnpm-lock.yaml:     lodash@4.17.21:\n    resolution: {integrity: sha512-x, tarball: <url>}
//   uv.lock:            [[package]]\nname = "jinja2"\nversion = "3.1.6"\nsdist = { url = "<url>" }
function lockfileArtifacts(path, text) {
    const basename = basenameOf(path).toLowerCase();
    const artifacts = [];
    // The identity includes the package and version the entry claims, so retargeting an
    // entry while keeping a base artifact (`lodash@4.17.20` -> `4.17.21` still resolving
    // the 4.17.20 tarball) is a new, unbound artifact rather than an existing one.
    const add = (key, claimed, value, bound) => artifacts.push({ key: `${key}\0${claimed}\0${value}`, bound });
    if (METADATA_LOCKFILE_BASENAMES.has(basename)) {
        const json = readJson(text);
        for (const [key, entry] of Object.entries(json?.packages ?? {})) {
            const index = key.lastIndexOf('node_modules/');
            if (index >= 0 && typeof entry?.resolved === 'string' && !entry.link) {
                const name = typeof entry.name === 'string' ? entry.name : key.slice(index + 'node_modules/'.length);
                add(key, `${name}@${entry.version}`, entry.resolved, isNpmTarballFor(entry.resolved, [name], entry.version));
            }
        }
        const visit = (dependencies, parent) => {
            for (const [key, entry] of Object.entries(dependencies ?? {})) {
                const path = `${parent}/${key}`;
                if (typeof entry?.resolved === 'string') {
                    const alias = String(entry.version ?? '').startsWith('npm:') ? splitNpmSpec(entry.version.slice(4)) : null;
                    const name = alias?.name ?? key;
                    const version = alias?.range ?? entry.version;
                    add(`v1${path}`, `${name}@${version}`, entry.resolved, isNpmTarballFor(entry.resolved, [name], version));
                }
                visit(entry?.dependencies, path);
            }
        };
        visit(json?.dependencies, '');
    } else if (basename === 'yarn.lock') {
        let header = '';
        let names = new Set();
        let version = '';
        for (const line of String(text ?? '').split(/\r?\n/)) {
            let match;
            if (/^\S/.test(line)) {
                header = line.startsWith('#') ? '' : line.trimEnd();
                names = new Set(header.replace(/:$/, '').split(',')
                    .map(spec => splitNpmSpec(spec.trim().replace(/^"|"$/g, '')))
                    .filter(spec => spec.name)
                    .map(spec => npmRealName(spec.name, spec.range)));
                version = '';
            } else if ((match = /^ {2}version:?\s+"?([^"\s]+)"?\s*$/.exec(line))) {
                version = match[1];
            } else if ((match = /^ {2}resolved\s+"?([^"\s]+)"?\s*$/.exec(line))) {
                add(header, version, match[1], isNpmTarballFor(match[1].split('#')[0], names, version));
            } else if ((match = /^ {2}resolution:\s+"?([^"\s]+)"?\s*$/.exec(line))) {
                const resolved = splitNpmSpec(match[1]);
                add(header, version, match[1], names.has(resolved.name) && resolved.range === `npm:${version}`);
            }
        }
    } else if (basename === 'pnpm-lock.yaml') {
        let entry = null;
        for (const line of String(text ?? '').split(/\r?\n/)) {
            let match;
            if ((match = /^ {2}(\S.*?):\s*$/.exec(line))) {
                const key = match[1].replace(/^'|'$/g, '').replace(/\(.*$/, '').replace(/^\//, '');
                const spec = splitNpmSpec(key);
                const slash = key.lastIndexOf('/');
                entry = spec.range ? spec : slash > 0 ? { name: key.slice(0, slash), range: key.slice(slash + 1) } : null;
            } else if (entry && (match = /^ {4}resolution:.*\btarball:\s*'?([^,'}\s]+)/.exec(line))) {
                add(`${entry.name}@${entry.range}`, '', match[1], isNpmTarballFor(match[1], [entry.name], entry.range));
            }
        }
    } else if (basename === 'uv.lock') {
        for (const block of String(text ?? '').split(/^\[\[package\]\]\s*$/m).slice(1)) {
            const body = block.split(/^\[/m)[0];
            const name = /^name\s*=\s*"([^"]+)"/m.exec(body)?.[1] ?? '';
            const version = /^version\s*=\s*"([^"]+)"/m.exec(body)?.[1] ?? '';
            for (const [, url] of body.matchAll(/\burl\s*=\s*"([^"]+)"/g)) {
                add(`${name}@${version}`, '', url, isPythonDistributionFor(url, name, version));
            }
        }
    }
    return artifacts;
}

// Artifacts the head lockfile adds that do not name the package and version their entry
// claims. Artifacts already present in the base are trusted as reviewed.
function unboundLockfileArtifacts(path, baseText, headText) {
    const existing = new Set(lockfileArtifacts(path, baseText).map(artifact => artifact.key));
    return lockfileArtifacts(path, headText).filter(artifact => !artifact.bound && !existing.has(artifact.key));
}

// pyproject.toml PEP 508 requirement strings (https://peps.python.org/pep-0508/):
//   "jinja2>=3.1.6", "jinja2[i18n]==3.1.6; python_version >= '3.9'", "jinja2 ~= 3.1.6, < 4"
// Only the lower or exact bound proves the version, so `<`, `<=` and `!=` are ignored.
function pyprojectEntries(text) {
    const entries = [];
    for (const [, requirement] of String(text ?? '').matchAll(/["']([A-Za-z0-9][A-Za-z0-9._-]*\s*(?:\[[^\]]*\])?\s*(?:===|==|~=|>=|>)[^"']*)["']/g)) {
        const name = /^[A-Za-z0-9][A-Za-z0-9._-]*/.exec(requirement)[0];
        for (const [, version] of requirement.split(';')[0].matchAll(/(?:===|==|~=|>=|>)\s*([^\s,;]+)/g)) {
            entries.push({ name, version });
        }
    }
    return entries;
}

// Directory.Packages.props: <PackageVersion Include="System.Text.Json" Version="9.0.5" />
// Target-framework item groups override an earlier entry with
//   <PackageVersion Update="Npgsql.EntityFrameworkCore.PostgreSQL" Version="9.0.4" />
// so both forms bind a version. The attributes may appear in either order.
function packagesPropsEntries(text) {
    const entries = [];
    for (const [element] of String(text ?? '').replace(/<!--[\s\S]*?-->/g, '').matchAll(/<PackageVersion\b[^>]*>/gi)) {
        const name = xmlAttribute(element, 'Include') ?? xmlAttribute(element, 'Update');
        const version = xmlAttribute(element, 'Version');
        if (name && version) {
            entries.push({ name, version: version.replace(/^\[|\]$/g, '') });
        }
    }
    return entries;
}

const MANIFEST_READERS = {
    'package.json': packageJsonEntries,
    'package-lock.json': packageLockEntries,
    'npm-shrinkwrap.json': packageLockEntries,
    'yarn.lock': yarnLockEntries,
    'pnpm-lock.yaml': pnpmLockEntries,
    'uv.lock': uvLockEntries,
    'pyproject.toml': pyprojectEntries,
    'directory.packages.props': packagesPropsEntries,
};

function manifestReader(path) {
    return MANIFEST_READERS[basenameOf(String(path ?? '')).toLowerCase()] ?? null;
}

function sameVersion(left, right) {
    return left === right || compareVersions(left, right) === 0;
}

/**
 * Every version the manifest at `path` binds to `name`, one per occurrence. Package
 * names compare case-insensitively, with pip names normalized per PEP 503. An
 * unrecognized manifest returns an empty list so callers fail closed.
 */
function manifestPackageVersions(path, text, ecosystem, name) {
    const reader = manifestReader(path);
    if (!reader) {
        return [];
    }
    const wanted = normalizePackageName(ecosystem, name);
    return reader(text)
        .filter(entry => normalizePackageName(ecosystem, entry.name) === wanted)
        .map(entry => entry.version);
}

/**
 * True when the manifest at `path` binds `name` to `version` in one of its own package
 * entries. Each manifest format is parsed so a version is only attributed to the entry
 * that declares it, never to a neighbouring package.
 */
function manifestMentionsVersion(path, text, ecosystem, name, version) {
    return manifestPackageVersions(path, text, ecosystem, name).some(found => sameVersion(found, version));
}

/**
 * Package version changes a PR makes in one manifest, as `{ name, from, to }` entries
 * with one entry per package and target version:
 * - a version bound on head but not on base. `from` lists the base versions it replaces:
 *   those removed by the PR, or, when the PR adds a copy and keeps the old ones, every
 *   base version of the package. It is empty for a package that is new to the file.
 * - a consolidation into a version the base already carries.
 * - a consumer that moves between two versions that both stay in the file, detected
 *   through the occurrence key or, for formats without one, through version counts.
 */
function manifestVersionChanges(path, baseText, headText, ecosystem) {
    const reader = manifestReader(path);
    if (!reader) {
        return [];
    }
    const group = text => {
        const packages = new Map();
        for (const { name, version, key } of reader(text)) {
            const id = normalizePackageName(ecosystem, name);
            if (!packages.has(id)) {
                packages.set(id, { name, counts: new Map(), keys: new Map() });
            }
            const entry = packages.get(id);
            entry.counts.set(version, (entry.counts.get(version) ?? 0) + 1);
            if (key !== undefined) {
                // A key seen twice is ambiguous and cannot pair base and head occurrences.
                entry.keys.set(key, entry.keys.has(key) ? null : version);
            }
        }
        return packages;
    };
    const base = group(baseText);
    const changes = new Map();
    const add = (name, from, to) => {
        const id = `${name}\u0000${to}`;
        if (!changes.has(id)) {
            changes.set(id, { name, from: [], to });
        }
        const change = changes.get(id);
        change.from = [...new Set([...change.from, ...from])];
    };
    for (const [id, { name, counts, keys }] of group(headText)) {
        const baseEntry = base.get(id);
        const baseCounts = baseEntry?.counts ?? new Map();
        const baseVersions = [...baseCounts.keys()];
        const removed = baseVersions.filter(version => !counts.has(version));
        const introduced = [...counts.keys()].filter(version => !baseCounts.has(version));
        for (const version of introduced) {
            add(name, removed.length ? removed : baseVersions, version);
        }
        // A consolidation into a version the base already carries introduces nothing new:
        // base `foo@1.0.0` + `foo@2.1.0` -> head `foo@2.1.0` still moves the 1.x consumers
        // to 2.1.0, so the surviving versions are the targets of the removed ones.
        if (!introduced.length && removed.length) {
            for (const version of counts.keys()) {
                add(name, removed, version);
            }
        }
        // Both versions can survive while a consumer moves between them: yarn selector
        // `foo@^1.5.0` resolving to 1.x on base and to the 2.x block on head.
        for (const [key, version] of keys) {
            const baseVersion = baseEntry?.keys.get(key);
            if (version !== null && baseVersion && baseVersion !== version) {
                add(name, [baseVersion], version);
            }
        }
        const shrunk = baseVersions.filter(version => counts.has(version) && counts.get(version) < baseCounts.get(version));
        for (const version of counts.keys()) {
            if (shrunk.length && baseCounts.has(version) && counts.get(version) > baseCounts.get(version)) {
                add(name, shrunk, version);
            }
        }
    }
    return [...changes.values()];
}

// A new version is breaking when any base version it replaces crosses a breaking
// boundary: a lockfile that consolidates `foo@1.x` and `foo@2.0` into `foo@2.1` moves
// the `1.x` consumers across a major version. A package new to the manifest has no
// predecessor and is left to the cooldown and source gates.
function isBreakingVersionChange(change) {
    return change.from.some(from => isBreakingChange(from, change.to));
}

/**
 * True when every occurrence of `update.name` in the alert's own manifest
 * (`alert.manifest_path`) is `acceptable` on the PR head, and at least one occurrence is
 * the update's new version. A lockfile can keep an old nested copy (`lodash@4.17.20` under
 * a parent) next to the bumped top-level one, so a single matching occurrence does not
 * prove the fix. Another manifest in the same directory (for example `yarn.lock` next to
 * an alerted `package-lock.json`) never counts, and an alerted manifest the PR leaves
 * unchanged is not in `headContents`, so it is not fixed.
 */
function alertManifestCarries(alert, ecosystem, update, headContents, acceptable) {
    const alertPath = String(alert.manifest_path ?? '').replace(/^\.?\/+/, '');
    const text = Object.hasOwn(headContents ?? {}, alertPath) ? headContents[alertPath] : null;
    if (!alertPath || text === null) {
        return false;
    }
    const versions = manifestPackageVersions(alertPath, text, ecosystem, update.name);
    return versions.some(version => sameVersion(version, update.to)) && versions.every(acceptable);
}

/**
 * True when `headText` differs from `baseText` only by swapping one version token on
 * lines that are dependency-version entries for the manifest at `path`, for example:
 *   package.json              "lodash": "^4.17.20",                       ->  "^4.17.21"
 *   Directory.Packages.props  <PackageVersion Include="X" Version="9.0.4" />  ->  "9.0.5"
 *   pyproject.toml            "requests>=2.31.0",                          ->  ">=2.32.3"
 * Added, removed, or otherwise edited lines fail, and so does a version-looking token
 * swap on any other line (a script argument such as `--mode=1`). For package.json the
 * parsed documents must also be identical outside the dependency maps. This keeps a
 * commit that only claims to be from Dependabot from slipping a script or build hook
 * change into an executable manifest. Missing text on either side fails closed.
 */
function isVersionOnlyEdit(path, baseText, headText) {
    if (typeof baseText !== 'string' || typeof headText !== 'string') {
        return false;
    }
    const baseLines = baseText.split(/\r?\n/);
    const headLines = headText.split(/\r?\n/);
    if (baseLines.length !== headLines.length
        || !baseLines.every((line, index) => line === headLines[index] || isDependencyVersionLineSwap(path, line, headLines[index]))) {
        return false;
    }
    if (basenameOf(path).toLowerCase() === 'pyproject.toml') {
        const arrays = pyprojectArrayKeys(baseLines);
        if (!baseLines.every((line, index) => line === headLines[index] || isPyprojectDependencyArray(arrays[index]))) {
            return false;
        }
    }
    if (basenameOf(path).toLowerCase() === 'package.json' && baseText !== headText) {
        const base = readJson(baseText);
        const head = readJson(headText);
        return base !== null && head !== null
            && jsonDifferencePaths(base, head).every(([section]) => PACKAGE_JSON_DEPENDENCY_MAPS.has(section));
    }
    return true;
}

/**
 * True when `baseLine` -> `headLine` swaps one version token inside the version value of a
 * dependency-version entry for the manifest at `path`. Everything before the version value
 * (the package key, an npm alias target, the PEP 508 name and extras, the NuGet `Include`)
 * must be unchanged, so `"x": "npm:a@1.0.0"` -> `"x": "npm:b@1.0.0"` or a key `"v1"` ->
 * `"v2"` is a package substitution, not a version edit. Unknown manifests fail closed.
 */
function isDependencyVersionLineSwap(path, baseLine, headLine) {
    const versionRange = DEPENDENCY_VERSION_LINE_RULES[basenameOf(path).toLowerCase()];
    const base = versionRange ? versionRange(baseLine) : null;
    const head = versionRange ? versionRange(headLine) : null;
    if (!base || !head || base.start !== head.start || baseLine.slice(0, base.start) !== headLine.slice(0, head.start)) {
        return false;
    }
    const span = versionTokenSwapSpan(baseLine, headLine);
    return span !== null && span.start >= base.start && span.baseEnd <= base.end && span.headEnd <= head.end;
}

/**
 * For each line of a pyproject.toml, the full dotted key of the multi-line array the line
 * sits in, or null. A PEP 508-looking string is a dependency only inside a dependency
 * collection; the same string in `[tool.example] arguments = [...]` is tool configuration.
 * Recognized shape (https://toml.io/en/v1.0.0#array):
 *   [project]
 *   dependencies = [
 *       "flask>=3.0.0",
 *   ]
 * Arrays opened and closed on one line, array-of-tables (`[[x]]`), and anything else the
 * scan does not recognize yield null, so those lines fail closed.
 */
function pyprojectArrayKeys(lines) {
    const unquote = key => key.split('.').map(part => part.trim().replace(/^(["'])(.*)\1$/, '$2')).join('.');
    let table = '';
    let array = null;
    return lines.map(line => {
        const trimmed = line.trim();
        if (array !== null) {
            const current = array;
            if (trimmed.startsWith(']')) {
                array = null;
            }
            return current;
        }
        const header = /^\[\s*([^[\]]+?)\s*\]\s*(?:#.*)?$/.exec(trimmed);
        if (header) {
            table = unquote(header[1]);
            return null;
        }
        if (/^\[\[/.test(trimmed)) {
            table = null;
            return null;
        }
        const opener = /^((?:[A-Za-z0-9_-]+|"[^"]*"|'[^']*')(?:\s*\.\s*(?:[A-Za-z0-9_-]+|"[^"]*"|'[^']*'))*)\s*=\s*\[\s*(?:#.*)?$/.exec(trimmed);
        if (opener && table !== null) {
            array = (table ? `${table}.` : '') + unquote(opener[1]);
        }
        return null;
    });
}

// PEP 621 dependencies and optional dependencies, PEP 735 dependency groups, and the uv
// dependency settings (https://docs.astral.sh/uv/reference/settings/).
function isPyprojectDependencyArray(key) {
    return key === 'project.dependencies'
        || /^project\.optional-dependencies\.[^.]+$/.test(key ?? '')
        || /^dependency-groups\.[^.]+$/.test(key ?? '')
        || ['tool.uv.dev-dependencies', 'tool.uv.constraint-dependencies', 'tool.uv.override-dependencies'].includes(key);
}

// Each rule returns the `[start, end)` index range of the line's version value, or null
// when the line is not a dependency-version entry.
const DEPENDENCY_VERSION_LINE_RULES = {
    'package.json': line => {
        const match = PACKAGE_JSON_VERSION_LINE.exec(line);
        const parts = match ? match[1].trim().split(/\s+/) : [];
        return parts.length > 0 && parts.every(part => NPM_RANGE_PART.test(part)) ? groupRange(match, 1) : null;
    },
    'pyproject.toml': line => groupRange(PYPROJECT_VERSION_LINE.exec(line), 2),
    'directory.packages.props': line => groupRange(PACKAGES_PROPS_VERSION_LINE.exec(line), 1),
};

// Index range of a capture group from a regex compiled with the `d` (hasIndices) flag.
function groupRange(match, group) {
    const indices = match?.indices?.[group];
    return indices ? { start: indices[0], end: indices[1] } : null;
}

// JSON paths (as key arrays) whose values differ between two parsed documents.
function jsonDifferencePaths(left, right, prefix = []) {
    const isObject = value => value !== null && typeof value === 'object';
    if (!isObject(left) || !isObject(right) || Array.isArray(left) !== Array.isArray(right)) {
        return Object.is(left, right) ? [] : [prefix];
    }
    const keys = new Set([...Object.keys(left), ...Object.keys(right)]);
    return [...keys].flatMap(key => !(key in left) || !(key in right)
        ? [[...prefix, key]]
        : jsonDifferencePaths(left[key], right[key], [...prefix, key]));
}

// The `{ start, baseEnd, headEnd }` span of the single version token that differs between
// the lines, or null when the lines differ by anything other than one version token.
function versionTokenSwapSpan(baseLine, headLine) {
    let start = 0;
    while (start < baseLine.length && start < headLine.length && baseLine[start] === headLine[start]) {
        start++;
    }
    let baseEnd = baseLine.length;
    let headEnd = headLine.length;
    while (baseEnd > start && headEnd > start && baseLine[baseEnd - 1] === headLine[headEnd - 1]) {
        baseEnd--;
        headEnd--;
    }
    // Widen the differing span to whole tokens so `1.2.3` -> `1.2.10` compares the full
    // versions. The shared suffix is identical on both sides, so widen both ends together.
    while (start > 0 && VERSION_TOKEN_CHAR.test(baseLine[start - 1])) {
        start--;
    }
    while (baseEnd < baseLine.length && VERSION_TOKEN_CHAR.test(baseLine[baseEnd])) {
        baseEnd++;
        headEnd++;
    }
    return VERSION_TOKEN.test(baseLine.slice(start, baseEnd)) && VERSION_TOKEN.test(headLine.slice(start, headEnd))
        ? { start, baseEnd, headEnd }
        : null;
}

function updateMatchesAlert(alert, ecosystem, update) {
    return alert.ecosystem === ecosystem
        && normalizePackageName(ecosystem, alert.package) === normalizePackageName(ecosystem, update.name);
}

/**
 * True when `update` provably fixes `alert`. Grouped Dependabot PRs can update the same
 * package in several directories, so a match on package name alone is not enough: the
 * alert's own manifest must carry the new version on the PR head, and every remaining occurrence of the package there must be at or above the
 * first patched version. `headContents` maps each changed manifest path to its head
 * text. Malware alerts are never counted as fixed (see `evaluateApprovalGates`).
 */
function alertFixedByUpdate(alert, ecosystem, update, headContents) {
    if (alert.malware || !updateMatchesAlert(alert, ecosystem, update)) {
        return false;
    }
    const patched = alert.first_patched_version;
    const comparison = patched ? compareVersions(update.to, patched) : null;
    if (comparison === null || comparison < 0) {
        return false;
    }
    // `first_patched_version` only bounds the range the installed version fell in; an
    // advisory can list disjoint vulnerable ranges, so every occurrence must also sit
    // outside all of them.
    return alertManifestCarries(alert, ecosystem, update, headContents,
        version => (compareVersions(version, patched) ?? -1) >= 0 && !inVulnerableRanges(version, alertVulnerableRanges(alert)));
}

// Both the approval job and the agent's pre-collected alerts.json carry every advisory
// range for the package as `vulnerable_ranges`. An alert with only the installed
// version's `vulnerable_version_range` falls back to that single range.
function alertVulnerableRanges(alert) {
    return alert.vulnerable_ranges ?? (alert.vulnerable_version_range ? [alert.vulnerable_version_range] : []);
}

/**
 * True when `version` falls in any GitHub advisory range, or a range cannot be evaluated
 * (fail closed). Ranges use the advisory database syntax: comma-separated comparator
 * terms that must all hold, for example `< 1.2.5`, `>= 1.3.0, < 1.3.4`, `= 2.0.0`.
 * See https://docs.github.com/rest/dependabot/alerts.
 */
function inVulnerableRanges(version, ranges) {
    return ranges.some(range => {
        const terms = String(range).split(',').map(term => term.trim()).filter(Boolean);
        if (!terms.length) {
            return true;
        }
        return terms.every(term => {
            const match = /^(<=|>=|<|>|=)\s*(\S+)$/.exec(term);
            const comparison = match ? compareVersions(version, match[2]) : null;
            if (comparison === null) {
                return true;
            }
            switch (match[1]) {
                case '<': return comparison < 0;
                case '<=': return comparison <= 0;
                case '>': return comparison > 0;
                case '>=': return comparison >= 0;
                default: return comparison === 0;
            }
        });
    });
}

/**
 * Alert numbers a Dependabot PR covers, so the agent does not duplicate the fix in the
 * auto-sec PR. Non-malware alerts use the same proof as the approval gate
 * (`alertFixedByUpdate`). Malware alerts have no patched version; they are covered only
 * when every occurrence of the flagged package in the alert's own manifest is the PR's new
 * version and that version is outside every vulnerable range (an alert without ranges is
 * never covered this way), and the approval gate still leaves those PRs to a human reviewer. Candidates
 * include `versionChanges` from the manifest diff, so a transitive upgrade that only
 * appears in a regenerated lockfile still counts, and so does a PR that drops the
 * alerted package from the alert's manifest entirely.
 */
function coveredAlerts(alerts, ecosystem, updates, versionChanges, headContents, baseContents) {
    if (!ecosystem) {
        return [];
    }
    const candidates = [...updates, ...versionChanges];
    return (alerts ?? [])
        .filter(alert => alertPackageRemoved(alert, ecosystem, baseContents, headContents)
            || candidates.some(update => alert.malware
                ? updateMatchesAlert(alert, ecosystem, update)
                    && alertManifestCarries(alert, ecosystem, update, headContents, version => sameVersion(version, update.to)
                        && alertVulnerableRanges(alert).length > 0
                        && !inVulnerableRanges(version, alertVulnerableRanges(alert)))
                : alertFixedByUpdate(alert, ecosystem, update, headContents)))
        .map(alert => alert.number)
        .sort((a, b) => a - b);
}

/**
 * True when the PR changes the alert's own manifest and removes every occurrence of the
 * alerted package from it, as when a parent upgrade drops a vulnerable transitive
 * dependency. `manifestVersionChanges` only reports versions present on head, so removals
 * are proven here instead. The base must carry the package; otherwise an empty head
 * result could just mean the manifest format is not parsed. The head manifest must also
 * still exist and parse to at least one package: a deleted, emptied, or malformed file
 * reads as zero occurrences too, and that is not a removal.
 */
function alertPackageRemoved(alert, ecosystem, baseContents, headContents) {
    if (alert.ecosystem !== ecosystem) {
        return false;
    }
    const alertPath = String(alert.manifest_path ?? '').replace(/^\.?\/+/, '');
    if (!alertPath || !Object.hasOwn(headContents ?? {}, alertPath) || !Object.hasOwn(baseContents ?? {}, alertPath)) {
        return false;
    }
    const headText = headContents[alertPath];
    const reader = manifestReader(alertPath);
    if (!reader || typeof headText !== 'string' || !headText.trim()
        || (alertPath.toLowerCase().endsWith('.json') && readJson(headText) === null)
        || reader(headText).length === 0) {
        return false;
    }
    return manifestPackageVersions(alertPath, baseContents[alertPath], ecosystem, alert.package).length > 0
        && manifestPackageVersions(alertPath, headText, ecosystem, alert.package).length === 0;
}

// Reasons the head commit's CI is not green: no check runs, any unfinished or
// unsuccessful check run, or any commit status other than success.
function ciReasons(checkRuns, statuses) {
    const reasons = [];
    if (!checkRuns.length) {
        reasons.push('no-checks');
    }
    if (checkRuns.some(run => run.status !== 'completed' || !SUCCESSFUL_CHECK_CONCLUSIONS.has(run.conclusion))) {
        reasons.push('checks-not-green');
    }
    if (statuses.some(status => status.state !== 'success')) {
        reasons.push('statuses-not-green');
    }
    return reasons;
}

async function fetchCiState(github, owner, repo, sha) {
    const checkRuns = (await github.paginate(github.rest.checks.listForRef, { owner, repo, ref: sha, per_page: 100 }))
        .map(run => ({ name: run.name, status: run.status, conclusion: run.conclusion }));
    // The combined status lists one (latest) status per context, 100 per page at most, so
    // read every page; a failing context on page two must still block approval. The
    // aggregate `state` is `pending` when no statuses exist, so it is only enforced when
    // `total_count` is non-zero.
    const statuses = [];
    let combinedState = null;
    let totalCount = 0;
    for (let page = 1; ; page++) {
        const { data: combined } = await github.rest.repos.getCombinedStatusForRef({ owner, repo, ref: sha, per_page: 100, page });
        const pageStatuses = combined.statuses ?? [];
        statuses.push(...pageStatuses.map(status => ({ context: status.context, state: status.state })));
        combinedState = combined.state ?? combinedState;
        totalCount = Number(combined.total_count ?? statuses.length);
        if (pageStatuses.length === 0 || statuses.length >= totalCount) {
            break;
        }
    }
    if (totalCount > 0 && combinedState !== 'success') {
        statuses.push({ context: 'combined', state: combinedState ?? 'pending' });
    }
    return { checkRuns, statuses };
}

/**
 * Pure gate evaluation for a single approval request.
 *
 * Returns `{ decision, reasons, fixedAlerts }` where decision is `approve` or `skip`.
 * Every reason is a short machine code so the run summary stays free of advisory detail.
 */
function evaluateApprovalGates(input) {
    const reasons = [];
    const { pr, expectedHeadSha, files, alerts, checkRuns, statuses, sourceChanges, headContents, packageInfo, reviews, now } = input;
    const baseContents = input.baseContents ?? {};
    const botLogin = input.botLogin ?? DEFAULT_BOT_LOGIN;

    if (pr.user_login !== DEPENDABOT_LOGIN) {
        reasons.push('not-dependabot');
    }
    if (pr.state !== 'open' || pr.draft) {
        reasons.push('not-open');
    }
    if (!expectedHeadSha || pr.head_sha !== expectedHeadSha) {
        reasons.push('head-sha-mismatch');
    }
    if (pr.base_ref !== BASE_BRANCH) {
        reasons.push('wrong-base-branch');
    }

    const ecosystem = ecosystemFromBranch(pr.head_ref);
    if (!ecosystem) {
        reasons.push('unknown-ecosystem');
    } else if (ecosystem === 'actions') {
        // Action pin bumps need a matching update to the repository Actions allow-list,
        // which a bot cannot confirm, so they always stay with a human reviewer.
        reasons.push('actions-allow-list-required');
    }

    if (!files.length) {
        reasons.push('no-files');
    }
    if (files.some(file => !isAllowedManifest(file.filename))) {
        reasons.push('non-manifest-file-changed');
    }
    // The filename check alone cannot prove the content is a version bump: package.json
    // scripts, pyproject.toml build hooks, and MSBuild targets are executable. Dependabot
    // only rewrites version nodes, so require every commit to be a GitHub-verified commit
    // authored by Dependabot; any pushed-on commit leaves the PR to a human reviewer.
    const commits = input.commits ?? [];
    if (!commits.length || commits.some(commit => commit.author_login !== DEPENDABOT_LOGIN || !commit.verified)) {
        reasons.push('non-dependabot-commit');
    }
    // Commit author metadata is caller-controlled and the verified bit only proves some
    // trusted key signed it, so also prove from the content that executable manifests
    // changed nothing but version tokens and that every new lockfile artifact is the
    // package and version its entry names.
    if (files.some(file => VERSION_ONLY_MANIFEST_BASENAMES.has(basenameOf(file.filename).toLowerCase())
        && !isVersionOnlyEdit(file.filename, baseContents[file.filename], headContents[file.filename]))) {
        reasons.push('non-version-manifest-edit');
    }
    if (files.some(file => unboundLockfileArtifacts(file.filename, baseContents[file.filename], headContents[file.filename]).length > 0)) {
        reasons.push('unbound-lockfile-artifact');
    }
    if (sourceChanges.some(change => change.newSources.length > 0)) {
        reasons.push('package-source-changed');
    }
    // `versionChanges` is every package version the diff introduces, including ones the
    // PR body does not list, so the gates below cannot be bypassed by an unlisted bump.
    const versionChanges = input.versionChanges ?? [];
    const tooManyVersionChanges = versionChanges.length > MAX_VERSION_CHANGES;
    if (tooManyVersionChanges) {
        reasons.push('too-many-version-changes');
    }

    const updates = parseDependabotUpdates(pr.title, pr.body);
    if (!updates.length) {
        reasons.push('no-parseable-updates');
    }
    if (updates.some(update => isBreakingChange(update.from, update.to))
        || versionChanges.some(isBreakingVersionChange)) {
        reasons.push('breaking-change');
    }
    const published = (name, version) => packageInfo[`${name}@${version}`]?.published_at;
    if (updates.some(update => !isCooldownSatisfied(published(update.name, update.to), now))
        || (!tooManyVersionChanges && versionChanges.some(change => !isCooldownSatisfied(published(change.name, change.to), now)))) {
        reasons.push('cooldown-not-satisfied');
    }

    // Malware alerts have no patched version, so no version check can prove the update
    // removes the flagged release. Fail closed and leave any PR touching one to a human,
    // including packages changed only by lockfile regeneration or removed outright.
    const touched = [...updates, ...versionChanges];
    if (ecosystem && alerts.some(alert => alert.malware
        && (touched.some(update => updateMatchesAlert(alert, ecosystem, update))
            || alertPackageRemoved(alert, ecosystem, baseContents, headContents)))) {
        reasons.push('malware-requires-review');
    }

    // Lockfile-only changes count too: a regenerated lockfile can upgrade the alerted
    // package transitively without the PR body listing it, or drop it entirely.
    const fixedAlerts = ecosystem
        ? alerts
            .filter(alert => !alert.malware && (
                touched.some(update => alertFixedByUpdate(alert, ecosystem, update, headContents))
                || alertPackageRemoved(alert, ecosystem, baseContents, headContents)))
            .map(alert => alert.number)
            .sort((a, b) => a - b)
        : [];
    if (!fixedAlerts.length) {
        reasons.push('fixes-no-open-alert');
    }

    reasons.push(...ciReasons(checkRuns, statuses));

    if (reviews.some(review => review.user_login === botLogin && review.state === 'APPROVED' && review.commit_id === pr.head_sha)) {
        reasons.push('already-approved');
    }

    return {
        decision: reasons.length === 0 ? 'approve' : 'skip',
        reasons,
        fixedAlerts,
    };
}

function npmPackagePath(name) {
    // Scoped names keep the `@` and encode the slash: @scope/pkg -> @scope%2fpkg.
    return name.startsWith('@') ? `@${encodeURIComponent(name.slice(1))}` : encodeURIComponent(name);
}

async function fetchJson(fetchImpl, url) {
    const response = await fetchImpl(url, { headers: { accept: 'application/json' } });
    if (response.status === 404) {
        return null;
    }
    if (!response.ok) {
        throw new Error(`GET ${url} failed with HTTP ${response.status}`);
    }
    return response.json();
}

function xmlAttribute(element, name) {
    const match = new RegExp(`\\b${name}\\s*=\\s*"([^"]*)"`, 'i').exec(element);
    return match ? match[1] : null;
}

function xmlSection(xml, tag) {
    const match = new RegExp(`<${tag}\\b[^>]*>([\\s\\S]*?)</${tag}>`, 'i').exec(xml);
    return match ? match[1] : '';
}

/**
 * Parse the parts of a NuGet.config that decide where a package restores from:
 *   <packageSources><add key="dotnet-public" value="https://.../index.json" /></packageSources>
 *   <packageSourceMapping><packageSource key="dotnet-public"><package pattern="*" /></packageSource></packageSourceMapping>
 *   <disabledPackageSources><add key="dotnet-public" value="true" /></disabledPackageSources>
 * Returns `{ sources: [{ key, url }], mapping: Map<key, pattern[]> }`.
 */
function parseNuGetConfig(xml) {
    const text = String(xml ?? '').replace(/<!--[\s\S]*?-->/g, '');
    const disabled = new Set();
    for (const [element] of xmlSection(text, 'disabledPackageSources').matchAll(/<add\b[^>]*>/gi)) {
        if ((xmlAttribute(element, 'value') ?? '').toLowerCase() === 'true') {
            disabled.add((xmlAttribute(element, 'key') ?? '').toLowerCase());
        }
    }

    const sources = [];
    for (const [element] of xmlSection(text, 'packageSources').matchAll(/<add\b[^>]*>/gi)) {
        const key = xmlAttribute(element, 'key');
        const url = xmlAttribute(element, 'value');
        if (key && url && !disabled.has(key.toLowerCase())) {
            sources.push({ key, url });
        }
    }

    const mapping = new Map();
    for (const match of xmlSection(text, 'packageSourceMapping').matchAll(/<packageSource\b([^>]*)>([\s\S]*?)<\/packageSource>/gi)) {
        const key = xmlAttribute(match[1], 'key');
        if (key) {
            const patterns = [...match[2].matchAll(/<package\b[^>]*>/gi)].map(([element]) => xmlAttribute(element, 'pattern')).filter(Boolean);
            mapping.set(key.toLowerCase(), patterns);
        }
    }

    return { sources, mapping };
}

// Package source mapping precedence
// (https://learn.microsoft.com/nuget/consume-packages/package-source-mapping#package-pattern-precedence):
// an exact package ID beats every prefix pattern, a longer `prefix*` beats a shorter one,
// and every source that declares the winning pattern is eligible. A trailing `*` is the
// only wildcard, so any other `*` is a literal character. Returns -1 when nothing matches.
function nugetPatternSpecificity(pattern, packageId) {
    const id = packageId.toLowerCase();
    const value = pattern.toLowerCase();
    if (value.endsWith('*')) {
        const prefix = value.slice(0, -1);
        return id.startsWith(prefix) ? prefix.length : -1;
    }
    return value === id ? Number.MAX_SAFE_INTEGER : -1;
}

// The sources NuGet would consult for `packageId`. Without package source mapping every
// enabled source is eligible.
function selectNuGetSources(config, packageId) {
    if (config.mapping.size === 0) {
        return config.sources;
    }

    let best = -1;
    let eligible = new Set();
    for (const [key, patterns] of config.mapping) {
        const specificity = Math.max(-1, ...patterns.map(pattern => nugetPatternSpecificity(pattern, packageId)));
        if (specificity > best) {
            best = specificity;
            eligible = new Set([key]);
        } else if (specificity === best && specificity >= 0) {
            eligible.add(key);
        }
    }
    return best < 0 ? [] : config.sources.filter(source => eligible.has(source.key.toLowerCase()));
}

// Resolve the flat-container base URL from a NuGet v3 service index, e.g.
//   { "resources": [{ "@id": "https://.../nuget/v3/flat2/", "@type": "PackageBaseAddress/3.0.0" }] }
async function getPackageBaseAddress(fetchImpl, serviceIndexUrl) {
    const index = await fetchJson(fetchImpl, serviceIndexUrl);
    const resource = (index?.resources ?? []).find(entry => String(entry?.['@type'] ?? '').startsWith('PackageBaseAddress/'));
    const address = resource?.['@id'];
    return typeof address === 'string' ? (address.endsWith('/') ? address : `${address}/`) : null;
}

async function isNuGetVersionAvailable(fetchImpl, nugetConfigText, id, normalizedVersion) {
    // Only the repository's own dnceng feeds count; a mapped source outside them can never
    // make a version "available" for an auto-sec bump.
    const sources = selectNuGetSources(parseNuGetConfig(nugetConfigText), id)
        .filter(source => APPROVED_SOURCE_PREFIXES.some(prefix => source.url.toLowerCase().startsWith(prefix)));
    for (const source of sources) {
        const baseAddress = await getPackageBaseAddress(fetchImpl, source.url);
        if (!baseAddress) {
            continue;
        }
        const versions = await fetchJson(fetchImpl, `${baseAddress}${id}/index.json`);
        if ((versions?.versions ?? []).some(entry => entry.toLowerCase() === normalizedVersion)) {
            return true;
        }
    }
    return false;
}

/**
 * Look up publish date and approved-feed availability for one package version.
 * Returns `{ ecosystem, name, version, published_at, cooldown_satisfied, available_on_approved_feed }`.
 *
 * NuGet availability follows the repository NuGet.config (`nugetConfigText`): only the
 * sources its package source mapping assigns to the package are probed. When no config
 * text is supplied, `available_on_approved_feed` is `null` (unknown). The approval gate
 * passes `checkAvailability: false`, since it needs only the publish date; availability
 * is then `null`.
 */
async function lookupPackageVersion(ecosystem, name, version, { fetchImpl = fetch, now = new Date(), nugetConfigText = null, checkAvailability = true } = {}) {
    let publishedAt = null;
    let available = null;

    switch (ecosystem) {
        case 'npm': {
            const packument = await fetchJson(fetchImpl, `https://registry.npmjs.org/${npmPackagePath(name)}`);
            publishedAt = packument?.time?.[version] ?? null;
            if (checkAvailability) {
                const mirror = await fetchJson(fetchImpl, `${APPROVED_NPM_REGISTRY}${npmPackagePath(name)}`);
                available = Boolean(mirror?.versions?.[version]);
            }
            break;
        }
        case 'pip': {
            // The repository's uv.lock files resolve from PyPI, so PyPI is the approved source.
            const release = await fetchJson(fetchImpl, `https://pypi.org/pypi/${encodeURIComponent(name)}/${encodeURIComponent(version)}/json`);
            // Files can be uploaded to a release later, and uv may pick a newer wheel, so the
            // cooldown runs from the newest upload so every installable file has aged.
            const uploads = (release?.urls ?? []).map(file => file.upload_time_iso_8601).filter(Boolean)
                .sort((a, b) => Date.parse(a) - Date.parse(b));
            publishedAt = uploads.at(-1) ?? null;
            available = checkAvailability ? release !== null : null;
            break;
        }
        case 'nuget': {
            const id = name.toLowerCase();
            const normalizedVersion = version.toLowerCase();
            const leaf = await fetchJson(fetchImpl, `https://api.nuget.org/v3/registration5-gz-semver2/${id}/${normalizedVersion}.json`);
            // Unlisting a version rewrites `published` to the 1900-01-01 sentinel, which would
            // look past any cooldown, so an unlisted version has no usable publish date:
            //   { "listed": false, "published": "1900-01-01T00:00:00+00:00" }
            // https://learn.microsoft.com/nuget/api/registration-base-url-resource#registration-leaf
            const published = leaf?.published ?? null;
            const unlisted = leaf?.listed === false || (published !== null && new Date(published).getUTCFullYear() <= 1900);
            publishedAt = unlisted ? null : published;
            available = checkAvailability && nugetConfigText ? await isNuGetVersionAvailable(fetchImpl, nugetConfigText, id, normalizedVersion) : null;
            break;
        }
        default:
            throw new Error(`Unsupported ecosystem '${ecosystem}'. Expected npm, pip, or nuget.`);
    }

    return {
        ecosystem,
        name,
        version,
        published_at: publishedAt,
        cooldown_satisfied: isCooldownSatisfied(publishedAt, now),
        available_on_approved_feed: available,
    };
}

function readApprovalRequests(agentOutput) {
    const items = Array.isArray(agentOutput?.items) ? agentOutput.items : [];
    const seen = new Set();
    const requests = [];
    for (const item of items) {
        if (item?.type !== 'approve_dependabot_pr') {
            continue;
        }
        const prNumber = Number(item.pr_number);
        const headSha = String(item.head_sha ?? '').trim().toLowerCase();
        if (!Number.isInteger(prNumber) || prNumber <= 0 || !/^[0-9a-f]{40}$/.test(headSha) || seen.has(prNumber)) {
            continue;
        }
        seen.add(prNumber);
        requests.push({ prNumber, headSha });
    }
    return requests.slice(0, MAX_EVALUATED_REQUESTS);
}

async function getFileText(github, owner, repo, path, ref) {
    try {
        const response = await github.rest.repos.getContent({ owner, repo, path, ref, mediaType: { format: 'raw' } });
        return typeof response.data === 'string' ? response.data : '';
    } catch (error) {
        if (error?.status === 404) {
            return '';
        }
        throw error;
    }
}

function normalizeAlert(alert) {
    const ecosystem = alert.dependency?.package?.ecosystem ?? '';
    const name = alert.dependency?.package?.name ?? '';
    // Keep every range the advisory lists for this package, not just the one matching
    // the installed version, so a target in a later vulnerable interval is rejected.
    const ranges = [
        alert.security_vulnerability?.vulnerable_version_range,
        ...(alert.security_advisory?.vulnerabilities ?? [])
            .filter(vulnerability => vulnerability.package?.ecosystem === ecosystem
                && normalizePackageName(ecosystem, vulnerability.package?.name ?? '') === normalizePackageName(ecosystem, name))
            .map(vulnerability => vulnerability.vulnerable_version_range),
    ].filter(range => typeof range === 'string' && range.trim());
    return {
        number: alert.number,
        ecosystem,
        package: name,
        manifest_path: alert.dependency?.manifest_path ?? '',
        first_patched_version: alert.security_vulnerability?.first_patched_version?.identifier ?? null,
        vulnerable_ranges: [...new Set(ranges)],
        malware: false,
    };
}

async function collectGateInput(github, owner, repo, request, { fetchImpl, now, botLogin, lookupCache = new Map() }) {
    const { data: pull } = await github.rest.pulls.get({ owner, repo, pull_number: request.prNumber });
    const pr = {
        number: pull.number,
        state: pull.state,
        draft: Boolean(pull.draft),
        user_login: pull.user?.login ?? '',
        head_sha: pull.head?.sha ?? '',
        head_ref: pull.head?.ref ?? '',
        base_sha: pull.base?.sha ?? '',
        base_ref: pull.base?.ref ?? '',
        title: pull.title ?? '',
        body: pull.body ?? '',
    };

    const files = (await github.paginate(github.rest.pulls.listFiles, { owner, repo, pull_number: pr.number, per_page: 100 }))
        .map(file => ({ filename: file.filename, status: file.status }));
    const commits = (await github.paginate(github.rest.pulls.listCommits, { owner, repo, pull_number: pr.number, per_page: 100 }))
        .map(commit => ({ author_login: commit.author?.login ?? '', verified: commit.commit?.verification?.verified === true }));

    // Compare full base/head file contents instead of the PR patch: GitHub omits the
    // patch for large lockfiles, and a missing patch must not hide a registry change.
    // The head text also proves which manifests an update actually touched.
    const sourceChanges = [];
    const headContents = {};
    const baseContents = {};
    const versionChangesByKey = new Map();
    const ecosystem = ecosystemFromBranch(pr.head_ref);
    for (const file of files.filter(entry => isAllowedManifest(entry.filename))) {
        const baseText = await getFileText(github, owner, repo, file.filename, pr.base_sha);
        const headText = await getFileText(github, owner, repo, file.filename, pr.head_sha);
        headContents[file.filename] = headText;
        baseContents[file.filename] = baseText;
        sourceChanges.push({ filename: file.filename, newSources: findNewSources(baseText, headText, file.filename) });
        if (ecosystem && ecosystem !== 'actions') {
            for (const change of manifestVersionChanges(file.filename, baseText, headText, ecosystem)) {
                const key = `${normalizePackageName(ecosystem, change.name)}@${change.to}`;
                const existing = versionChangesByKey.get(key);
                if (existing) {
                    existing.from = [...new Set([...existing.from, ...change.from])];
                } else {
                    versionChangesByKey.set(key, { ...change, from: [...change.from] });
                }
            }
        }
    }
    const versionChanges = [...versionChangesByKey.values()];

    const alerts = (await github.paginate('GET /repos/{owner}/{repo}/dependabot/alerts', { owner, repo, state: 'open', per_page: 100 }))
        .map(normalizeAlert);
    const malware = await github.paginate('GET /repos/{owner}/{repo}/dependabot/alerts', { owner, repo, state: 'open', classification: 'malware', per_page: 100 });
    const malwareNumbers = new Set(malware.map(alert => alert.number));
    for (const alert of alerts) {
        alert.malware = malwareNumbers.has(alert.number);
    }

    const { checkRuns, statuses } = await fetchCiState(github, owner, repo, pr.head_sha);

    const reviews = (await github.paginate(github.rest.pulls.listReviews, { owner, repo, pull_number: pr.number, per_page: 100 }))
        .map(review => ({ user_login: review.user?.login ?? '', state: review.state, commit_id: review.commit_id }));

    const packageInfo = {};
    if (ecosystem && ecosystem !== 'actions') {
        const targets = parseDependabotUpdates(pr.title, pr.body).map(update => ({ name: update.name, to: update.to }));
        // Past the cap the gate already fails, so skip one registry request per change.
        if (versionChanges.length <= MAX_VERSION_CHANGES) {
            targets.push(...versionChanges);
        }
        for (const { name, to } of targets) {
            const key = `${name}@${to}`;
            if (key in packageInfo) {
                continue;
            }
            const cacheKey = `${ecosystem}:${normalizePackageName(ecosystem, name)}@${to}`;
            try {
                if (!lookupCache.has(cacheKey)) {
                    lookupCache.set(cacheKey, await lookupPackageVersion(ecosystem, name, to, { fetchImpl, now, checkAvailability: false }));
                }
                packageInfo[key] = lookupCache.get(cacheKey);
            } catch {
                // A failed lookup leaves the entry missing, which fails the cooldown gate closed.
            }
        }
    }

    return { pr, expectedHeadSha: request.headSha, files, commits, alerts, checkRuns, statuses, sourceChanges, headContents, baseContents, versionChanges, packageInfo, reviews, now, botLogin };
}

/**
 * Entry point for the `approve-dependabot-pr` safe-output job (actions/github-script).
 * Reads approval requests from the agent output, re-verifies all gates, and approves
 * only the PRs that pass. Staged mode logs decisions without submitting reviews.
 *
 * `github` performs all reads with the job's GITHUB_TOKEN (which carries
 * `security-events: read` for Dependabot alerts); `approver` is an Octokit client for
 * the Aspire bot App and is used only to submit the APPROVE review.
 */
async function runApprovalJob({ github, approver = github, context, core, fs = require('node:fs'), env = process.env, fetchImpl = fetch, now = new Date() }) {
    const outputPath = env.GH_AW_AGENT_OUTPUT;
    if (!outputPath || !fs.existsSync(outputPath)) {
        core.info('No agent output found; nothing to approve.');
        return [];
    }

    const requests = readApprovalRequests(JSON.parse(fs.readFileSync(outputPath, 'utf8')));
    const staged = env.GH_AW_SAFE_OUTPUTS_STAGED === 'true';
    const botLogin = env.AUTO_SEC_BOT_LOGIN || DEFAULT_BOT_LOGIN;
    const { owner, repo } = context.repo;
    const results = [];
    const lookupCache = new Map();
    let approvals = 0;

    for (const request of requests) {
        let result;
        if (approvals >= MAX_APPROVALS) {
            results.push({ pr: request.prNumber, decision: 'skip', reasons: ['approval-limit-reached'], fixedAlerts: [] });
            core.info(`#${request.prNumber}: skip approval-limit-reached`);
            continue;
        }
        try {
            const input = await collectGateInput(github, owner, repo, request, { fetchImpl, now, botLogin, lookupCache });
            result = { pr: request.prNumber, ...evaluateApprovalGates(input) };
        } catch (error) {
            result = { pr: request.prNumber, decision: 'skip', reasons: ['gate-evaluation-failed'], fixedAlerts: [] };
            core.warning(`Gate evaluation failed for #${request.prNumber}: ${error.message}`);
        }

        if (result.decision === 'approve' && !staged) {
            // The gates take many requests; Dependabot can rebase meanwhile, and an
            // approval on the older commit could still count for the new head, or the PR can
            // be retargeted away from the branch its alerts describe.
            const { data: live } = await github.rest.pulls.get({ owner, repo, pull_number: request.prNumber });
            // It can also be closed or converted to draft; an approval submitted then would
            // still count once the PR is reopened or marked ready.
            if (live.head?.sha !== request.headSha) {
                result = { ...result, decision: 'skip', reasons: ['head-sha-mismatch'] };
            } else if (live.state !== 'open' || live.draft) {
                result = { ...result, decision: 'skip', reasons: ['not-open'] };
            } else if (live.base?.ref !== BASE_BRANCH) {
                result = { ...result, decision: 'skip', reasons: ['wrong-base-branch'] };
            } else {
                // CI was read before the registry lookups; a re-run or late status can turn
                // the same SHA pending or red in the meantime, so read it again right before
                // the review is submitted.
                const liveCi = await fetchCiState(github, owner, repo, request.headSha);
                const ci = ciReasons(liveCi.checkRuns, liveCi.statuses);
                if (ci.length > 0) {
                    result = { ...result, decision: 'skip', reasons: ci };
                }
            }
        }
        if (result.decision === 'approve') {
            approvals++;
        }
        if (result.decision === 'approve' && !staged) {
            await approver.rest.pulls.createReview({
                owner,
                repo,
                pull_number: request.prNumber,
                commit_id: request.headSha,
                event: 'APPROVE',
                body: 'Automated dependency review: this update passed the auto-sec checks (package sources unchanged, checks green, non-breaking, and past the 7-day cooldown).',
            });
        }

        results.push(result);
        core.info(`#${result.pr}: ${result.decision}${staged ? ' (staged)' : ''} ${publicReasons(result.reasons).join(',')}`);
    }

    const approved = results.filter(result => result.decision === 'approve').length;
    await core.summary
        .addHeading('auto-sec Dependabot approvals', 3)
        .addRaw(`Requests: ${results.length}. Approved: ${approved}${staged ? ' (staged)' : ''}. Skipped: ${results.length - approved}.\n\n`)
        .addRaw(results.map(result => `- #${result.pr}: ${result.decision}${result.reasons.length ? ` (${publicReasons(result.reasons).join(', ')})` : ''}`).join('\n'))
        .write();

    return results;
}

// The run log and job summary are public. Reasons that would reveal which PR touches an
// alert class the workflow keeps private are reported under a generic code.
const PUBLIC_REASON_CODES = new Map([['malware-requires-review', 'human-review-required']]);

function publicReasons(reasons) {
    return [...new Set(reasons.map(reason => PUBLIC_REASON_CODES.get(reason) ?? reason))];
}

/**
 * Runs in the safe_outputs job before the push handler. gh-aw's push-to-pull-request-branch
 * needs `target: "*"` on a scheduled run and only filters by label and title prefix, so a
 * mislabeled PR on another branch would otherwise be pushable. Every requested push must
 * name an open PR whose head is AUTO_SEC_BRANCH in this repository; any other request
 * fails the step, which stops the job before the handler pushes anything.
 */
async function runPushTargetGate({ github, context, core, fs = require('node:fs'), env = process.env }) {
    const outputPath = env.GH_AW_AGENT_OUTPUT;
    const agentOutput = outputPath && fs.existsSync(outputPath) ? JSON.parse(fs.readFileSync(outputPath, 'utf8')) : {};
    const items = (Array.isArray(agentOutput?.items) ? agentOutput.items : []).filter(item => item?.type === 'push_to_pull_request_branch');
    const { owner, repo } = context.repo;
    const fullName = `${owner}/${repo}`.toLowerCase();
    const violations = [];

    for (const item of items) {
        const prNumber = Number(item.pull_request_number);
        if (!Number.isInteger(prNumber) || prNumber <= 0) {
            violations.push({ pr: null, reason: 'missing-pull-request-number' });
            continue;
        }
        const { data: pr } = await github.rest.pulls.get({ owner, repo, pull_number: prNumber });
        if (pr.state !== 'open') {
            violations.push({ pr: prNumber, reason: 'not-open' });
        } else if (pr.head?.ref !== AUTO_SEC_BRANCH) {
            violations.push({ pr: prNumber, reason: 'wrong-head-branch' });
        } else if (String(pr.head?.repo?.full_name ?? '').toLowerCase() !== fullName) {
            violations.push({ pr: prNumber, reason: 'wrong-head-repository' });
        } else if (pr.base?.ref !== BASE_BRANCH) {
            violations.push({ pr: prNumber, reason: 'wrong-base-branch' });
        }
    }

    if (violations.length > 0) {
        core.setFailed(`auto-sec pushes may only target the open ${AUTO_SEC_BRANCH} pull request: ${violations.map(v => `${v.pr === null ? 'unknown' : `#${v.pr}`} ${v.reason}`).join('; ')}`);
    } else {
        core.info(`Push target gate passed for ${items.length} request(s).`);
    }
    return { requests: items.length, violations };
}

// Split a `git format-patch` file into per-file diffs. Commit headers and messages before
// the first `diff --git` line are skipped:
//   diff --git a/extension/package.json b/extension/package.json
//   index 1111111..2222222 100644
//   --- a/extension/package.json
//   +++ b/extension/package.json
//   @@ -10,7 +10,7 @@
//      "dependencies": {
//   -    "lodash": "^4.17.20",
//   +    "lodash": "^4.17.21",
// Hunk bodies are consumed by the line counts in their `@@` header, so a hunk can never
// swallow the next `diff --git` header. Each file diff records its `index <old>..<new>`
// blob IDs, its hunks (for rebuilding the patched file), and its change blocks (runs of
// removed then added lines between context lines).
function parsePatchFileDiffs(patchText) {
    const diffs = [];
    let current = null;
    const lines = String(patchText ?? '').split('\n').map(line => line.replace(/\r$/, ''));
    for (let index = 0; index < lines.length; index++) {
        const line = lines[index];
        if (line.startsWith('diff --git ')) {
            const paths = /^diff --git a\/(\S+) b\/(\S+)$/.exec(line);
            current = { oldPath: paths?.[1] ?? null, newPath: paths?.[2] ?? null, parseable: Boolean(paths), metadata: [], oldBlob: null, hunks: [], blocks: [] };
            diffs.push(current);
            continue;
        }
        if (!current) {
            continue;
        }
        const hunk = /^@@ -(\d+)(?:,(\d+))? \+\d+(?:,(\d+))? @@/.exec(line);
        if (!hunk) {
            const blobs = /^index ([0-9a-f]+)\.\.([0-9a-f]+)/.exec(line);
            if (blobs) {
                current.oldBlob = blobs[1];
            } else if (/^(?:new file mode|deleted file mode|old mode|new mode|rename from|rename to|copy from|copy to|Binary files|GIT binary patch)/.test(line)) {
                current.metadata.push(line);
            }
            continue;
        }
        let oldRemaining = hunk[2] === undefined ? 1 : Number(hunk[2]);
        let newRemaining = hunk[3] === undefined ? 1 : Number(hunk[3]);
        const parsedHunk = { oldStart: Number(hunk[1]), oldCount: oldRemaining, lines: [] };
        current.hunks.push(parsedHunk);
        let block = null;
        while ((oldRemaining > 0 || newRemaining > 0) && index + 1 < lines.length) {
            const body = lines[index + 1];
            const marker = body[0];
            if (marker === '\\') {
                index++;
                continue;
            }
            if (marker === ' ' || body === '') {
                block = null;
                parsedHunk.lines.push({ marker: ' ', text: body.slice(1) });
                oldRemaining--;
                newRemaining--;
            } else if (marker === '-' || marker === '+') {
                if (!block || (marker === '-' && block.added.length > 0)) {
                    block = { removed: [], added: [] };
                    current.blocks.push(block);
                }
                (marker === '-' ? block.removed : block.added).push(body.slice(1));
                parsedHunk.lines.push({ marker, text: body.slice(1) });
                marker === '-' ? oldRemaining-- : newRemaining--;
            } else {
                break;
            }
            index++;
        }
    }
    return diffs;
}

// Applies parsed hunks to `baseText` at their stated line numbers, requiring every context
// and removed line to match exactly. Returns the patched text, or null when it does not apply.
function applyHunks(baseText, hunks) {
    const base = baseText.split('\n');
    const out = [];
    let position = 0;
    for (const hunk of hunks) {
        // `@@ -N,0` names the line after which the insertion happens.
        const start = hunk.oldCount === 0 ? hunk.oldStart : hunk.oldStart - 1;
        if (start < position || start > base.length) {
            return null;
        }
        out.push(...base.slice(position, start));
        position = start;
        for (const { marker, text } of hunk.lines) {
            if (marker === '+') {
                out.push(text);
                continue;
            }
            if (base[position] !== text) {
                return null;
            }
            if (marker === ' ') {
                out.push(text);
            }
            position++;
        }
    }
    out.push(...base.slice(position));
    return out.join('\n');
}

// Git's object ID for a blob, used to match the `index <old>..<new>` line of a patch to the
// exact file content it was generated from. This is git's identity scheme, not a security
// hash: https://git-scm.com/book/en/v2/Git-Internals-Git-Objects
function gitBlobId(text) {
    const body = Buffer.from(text, 'utf8');
    return require('node:crypto').createHash('sha1').update(`blob ${body.length}\0`).update(body).digest('hex');
}

/**
 * Checks the contents of an agent patch before the create or push handler applies it.
 * The handler's file allowlist admits executable manifests, so each changed package.json,
 * pyproject.toml, or Directory.Packages.props is rebuilt in full: the base is the candidate
 * from `readBaseTexts(path)` whose git blob ID matches the diff's `index` line, the hunks
 * must apply to it exactly, and `isVersionOnlyEdit` must accept the result (which, for
 * package.json, also requires the parsed documents to differ only inside dependency maps,
 * so `"preinstall": "1.0.0"` -> `"1.0.1"` fails). No file may add a package source that is
 * neither approved nor already referenced, outside package metadata, by the lines it
 * replaces or by the exact base the diff names (see `findNewSources`). No added line may contain advisory
 * text, and comment-capable lockfiles may not gain comments. Every other file must also
 * rebuild in full, and every new lockfile artifact must name the package and version its
 * entry claims (see `unboundLockfileArtifacts`). Each rebuilt file's original base and
 * final head are recorded in `rebuiltFiles`. Returns `{ path, reason }` violations.
 */
function checkPatchContents(patchText, readBaseTexts = () => [], rebuiltFiles = new Map()) {
    const violations = [];
    // A later commit in the same patch applies on top of the file an earlier one produced.
    const rebuilt = new Map();
    for (const diff of parsePatchFileDiffs(patchText)) {
        const path = diff.newPath ?? diff.oldPath ?? '(unknown)';
        if (!diff.parseable || diff.oldPath !== diff.newPath) {
            violations.push({ path, reason: 'unsupported-file-diff' });
            continue;
        }
        if (diff.metadata.length > 0) {
            violations.push({ path, reason: 'unsupported-file-diff' });
            continue;
        }
        const candidates = [...(rebuilt.get(path) ?? []), ...(readBaseTexts(path) ?? [])].filter(text => typeof text === 'string');
        // Only the exact file the diff was generated from (its `index` blob ID) may authorize
        // anything: a stale auto-sec branch copy must not vouch for a patch based on main.
        const base = diff.oldBlob ? candidates.find(text => gitBlobId(text).startsWith(diff.oldBlob)) : undefined;
        const normalizedBase = base?.replace(/\r\n/g, '\n');
        const head = normalizedBase === undefined ? null : applyHunks(normalizedBase, diff.hunks);
        if (VERSION_ONLY_MANIFEST_BASENAMES.has(basenameOf(path).toLowerCase())
            && (head === null || !isVersionOnlyEdit(path, normalizedBase, head))) {
            violations.push({ path, reason: 'non-version-manifest-edit' });
        } else if (head === null) {
            // Lockfile artifacts and introduced versions can only be proven from the full file.
            violations.push({ path, reason: 'unreconstructable-file-diff' });
        } else {
            rebuilt.set(path, [head, ...(rebuilt.get(path) ?? [])]);
            if (unboundLockfileArtifacts(path, normalizedBase, head).length > 0) {
                violations.push({ path, reason: 'unbound-lockfile-artifact' });
            }
            // The first diff of a path names the file the whole patch series started from.
            rebuiltFiles.set(path, { base: rebuiltFiles.get(path)?.base ?? normalizedBase, head });
        }
        // Each block's added lines are contiguous in the new file; the break line keeps the
        // metadata scanner from reading separate blocks (or files) as one JSON fragment.
        // Without a matching base, only the lines the diff replaces can vouch for a source.
        const segmentBreak = '\0';
        const removed = diff.blocks.map(block => block.removed.join('\n')).join(`\n${segmentBreak}\n`);
        const added = diff.blocks.map(block => block.added.join('\n')).join(`\n${segmentBreak}\n`);
        if (findNewSources([...(normalizedBase === undefined ? [] : [normalizedBase]), removed].join(`\n${segmentBreak}\n`), added, path, segmentBreak).length > 0) {
            violations.push({ path, reason: 'new-package-source' });
        }
        // The patch becomes public, so no added line may carry advisory text, and lockfiles,
        // which skip the version-only check, may not gain comments to carry free text.
        const addedLines = diff.blocks.flatMap(block => block.added);
        if (addedLines.some(line => PUBLIC_FORBIDDEN_TOKEN.test(line))) {
            violations.push({ path, reason: 'forbidden-public-text' });
        }
        if (COMMENT_LOCKFILE_BASENAMES.has(basenameOf(path).toLowerCase()) && addedLines.some(hasLockfileComment)) {
            violations.push({ path, reason: 'lockfile-comment' });
        }
    }
    return violations;
}

// Lockfiles whose formats allow `#` comments: yarn.lock (https://classic.yarnpkg.com/lang/en/docs/yarn-lock/),
// pnpm-lock.yaml (YAML), and uv.lock (TOML). JSON lockfiles have no comment syntax.
const COMMENT_LOCKFILE_BASENAMES = new Set(['yarn.lock', 'pnpm-lock.yaml', 'uv.lock']);

// True when the line has a `#` comment outside quoted strings, for example
//   # note        or   version = "1.0.0" # note
// while `resolved "https://x/y.tgz#sha1"` (a quoted `#`) is not a comment.
function hasLockfileComment(line) {
    return /(?:^|\s)#/.test(line.replace(/"(?:[^"\\]|\\.)*"|'[^']*'/g, '""'));
}

// The ecosystem a manifest or lockfile resolves from, keyed by basename.
const PATH_ECOSYSTEMS = new Map([
    ['package.json', 'npm'],
    ['package-lock.json', 'npm'],
    ['npm-shrinkwrap.json', 'npm'],
    ['yarn.lock', 'npm'],
    ['pnpm-lock.yaml', 'npm'],
    ['pyproject.toml', 'pip'],
    ['uv.lock', 'pip'],
    ['directory.packages.props', 'nuget'],
]);

/**
 * Re-derives every package version an agent patch introduces (base vs rebuilt head of each
 * file, via `manifestVersionChanges`) and enforces the policy the prompt states: no breaking
 * change, a publish date past the 7-day cooldown, and, for NuGet, availability through the
 * repository NuGet.config package source mapping. A failed lookup fails closed. Returns
 * `{ path, reason }` violations.
 */
async function checkPatchVersionPolicy(rebuiltFiles, { fetchImpl = fetch, now = new Date(), nugetConfigText = null } = {}) {
    const violations = [];
    const changes = [];
    for (const [path, { base, head }] of rebuiltFiles) {
        const ecosystem = PATH_ECOSYSTEMS.get(basenameOf(path).toLowerCase());
        if (ecosystem) {
            changes.push(...manifestVersionChanges(path, base, head, ecosystem).map(change => ({ path, ecosystem, change })));
        }
    }
    if (changes.length > MAX_VERSION_CHANGES) {
        return [{ path: '(patch)', reason: 'too-many-version-changes' }];
    }
    const lookups = new Map();
    for (const { path, ecosystem, change } of changes) {
        if (isBreakingVersionChange(change)) {
            violations.push({ path, reason: 'breaking-change' });
        }
        const key = `${ecosystem}:${normalizePackageName(ecosystem, change.name)}@${change.to}`;
        if (!lookups.has(key)) {
            lookups.set(key, await lookupPackageVersion(ecosystem, change.name, change.to, { fetchImpl, now, nugetConfigText, checkAvailability: ecosystem === 'nuget' })
                .catch(() => null));
        }
        const info = lookups.get(key);
        if (!info?.cooldown_satisfied) {
            violations.push({ path, reason: 'cooldown-not-satisfied' });
        }
        if (ecosystem === 'nuget' && info?.available_on_approved_feed !== true) {
            violations.push({ path, reason: 'nuget-not-on-approved-feed' });
        }
    }
    return violations;
}

/**
 * Runs in the safe_outputs job before the create and push handlers. Both outputs use the
 * `am` patch transport, so the `aw-*.patch` files under `patchDir` are exactly what the
 * handlers apply; a bundle file would bypass this check and fails the step instead. A
 * create patch is based on the checked-out workspace and a push patch on the auto-sec
 * branch, so both versions of each changed file are base candidates.
 */
async function runPatchContentGate({ core, github = null, context = null, fs = require('node:fs'), patchDir = '/tmp/gh-aw', workspace = process.env.GITHUB_WORKSPACE, fetchImpl = fetch, now = new Date() }) {
    const path = require('node:path');
    const names = fs.existsSync(patchDir) ? fs.readdirSync(patchDir).filter(name => /^aw-.*\.(?:patch|bundle)$/.test(name)).sort() : [];
    const patches = new Map(names.filter(name => name.endsWith('.patch')).map(name => [name, fs.readFileSync(path.join(patchDir, name), 'utf8')]));
    const branchTexts = new Map();
    if (github && context) {
        const changed = new Set([...patches.values()].flatMap(text => parsePatchFileDiffs(text).map(diff => diff.oldPath).filter(Boolean)));
        for (const file of changed) {
            try {
                const { data } = await github.rest.repos.getContent({ ...context.repo, path: file, ref: AUTO_SEC_BRANCH });
                if (typeof data?.content === 'string') {
                    branchTexts.set(file, Buffer.from(data.content, 'base64').toString('utf8'));
                }
            } catch (error) {
                // No auto-sec branch yet, or the file is not on it.
                if (error?.status !== 404) {
                    throw error;
                }
            }
        }
    }
    const readBaseTexts = file => {
        const full = workspace ? path.resolve(workspace, file) : null;
        const local = full && full.startsWith(path.resolve(workspace) + path.sep) && fs.existsSync(full) ? fs.readFileSync(full, 'utf8') : undefined;
        return [local, branchTexts.get(file)];
    };
    const violations = [];
    const rebuiltFiles = new Map();
    for (const name of names) {
        if (name.endsWith('.bundle')) {
            violations.push({ path: name, reason: 'bundle-transport' });
            continue;
        }
        violations.push(...checkPatchContents(patches.get(name), readBaseTexts, rebuiltFiles));
    }
    // The handlers push before any human review and same-repository PR CI installs the
    // result, so the version policy the prompt states is re-checked here from the rebuilt
    // files instead of trusting the agent's lookups.
    if (violations.length === 0) {
        const nugetConfig = workspace ? path.resolve(workspace, 'NuGet.config') : null;
        const nugetConfigText = nugetConfig && fs.existsSync(nugetConfig) ? fs.readFileSync(nugetConfig, 'utf8') : null;
        violations.push(...await checkPatchVersionPolicy(rebuiltFiles, { fetchImpl, now, nugetConfigText }));
    }
    if (violations.length > 0) {
        core.setFailed(`auto-sec patch content gate failed: ${violations.map(v => `${v.path} ${v.reason}`).join('; ')}`);
    } else {
        core.info(`Patch content gate passed for ${names.length} patch file(s).`);
    }
    return { patches: names.length, violations };
}

// The agent sees malware flags, advisory ranges, and alert numbers, but everything it
// publishes must name packages and versions only. The prompt asks for fixed text; these
// templates enforce it, so free text from a confused or prompt-injected run never reaches
// a public PR title, body, or commit.
const PUBLIC_PR_TITLE = 'Automated dependency updates';
const PUBLIC_PR_BODY_HEAD = [
    'This is an automated pull request created by the auto-sec workflow.',
    '',
    'It updates the following dependencies to newer, non-breaking versions that have',
    'been published for at least 7 days and resolve from the existing package sources:',
    '',
    '| Package | Manifest | From | To |',
    '| --- | --- | --- | --- |',
];
const PUBLIC_PR_BODY_TAIL = [
    '',
    'No package sources or feeds were changed. Please review the lockfile diffs and',
    'CI results before merging.',
];
const PUBLIC_COMMIT_SUBJECT = 'Update dependencies';
const PUBLIC_COMMIT_IDENTITY = 'github-actions[bot] <github-actions[bot]@users.noreply.github.com>';
const MAX_PUBLIC_ROWS = 200;
// npm (`@scope/name`), PyPI, and NuGet (`Microsoft.Extensions.AI`) package names.
const PUBLIC_PACKAGE_NAME = /^(?:@[A-Za-z0-9][\w.-]*\/)?[A-Za-z0-9][\w.+-]{0,213}$/;
// A version or simple range such as `4.17.21`, `^4.17.21`, `>=1.2.3`, or `9.0.0-rc.1`.
const PUBLIC_VERSION = /^(?:[~^=]|[<>]=?)?v?[0-9][0-9A-Za-z.+_-]{0,63}$/;
const PUBLIC_MANIFEST_PATH = /^[\w.@+-]+(?:\/[\w.@+-]+)*\/?$/;
// Defense in depth for tokens the templates could still carry inside a name-shaped cell.
const PUBLIC_FORBIDDEN_TOKEN = /\b(?:GHSA|CVE|vulnerab\w*|exploit\w*|malware|malicious|advisory)\b/i;

// gh-aw sanitization wraps `@mentions` (which scoped npm names look like) in backticks, so
// one pair of surrounding backticks is accepted on any cell.
function publicCell(text) {
    const trimmed = String(text).trim();
    return /^`[^`]*`$/.test(trimmed) ? trimmed.slice(1, -1) : trimmed;
}

// Parses one `<name> <from> -> <to>` commit body line or one
// `| <name> | <manifest> | <from> | <to> |` PR body table row. Returns null when it does
// not match exactly.
function parsePublicUpdate(text, kind) {
    if (kind === 'commit') {
        const match = /^(\S+) (\S+) -> (\S+)$/.exec(text);
        if (!match) {
            return null;
        }
        const [, name, from, to] = match;
        return PUBLIC_PACKAGE_NAME.test(name) && PUBLIC_VERSION.test(from) && PUBLIC_VERSION.test(to) && !PUBLIC_FORBIDDEN_TOKEN.test(text) ? { name, to } : null;
    }
    const match = /^\|([^|]*)\|([^|]*)\|([^|]*)\|([^|]*)\|$/.exec(text);
    if (!match) {
        return null;
    }
    const [name, manifest, from, to] = match.slice(1).map(publicCell);
    return PUBLIC_PACKAGE_NAME.test(name) && PUBLIC_MANIFEST_PATH.test(manifest) && PUBLIC_VERSION.test(from) && PUBLIC_VERSION.test(to) && !PUBLIC_FORBIDDEN_TOKEN.test(text) ? { name, to } : null;
}

function publicLines(text) {
    return String(text ?? '').replace(/\r\n/g, '\n').trim().split('\n').map(line => line.trimEnd());
}

// A commit message must be `Update dependencies`, optionally followed by a blank line and
// one `<name> <from> -> <to>` line per package.
function checkPublicCommitMessage(text) {
    const [subject, ...rest] = publicLines(text);
    if (subject !== PUBLIC_COMMIT_SUBJECT) {
        return false;
    }
    if (rest.length === 0) {
        return true;
    }
    const [separator, ...updates] = rest;
    return separator === '' && updates.length <= MAX_PUBLIC_ROWS && updates.every(line => parsePublicUpdate(line, 'commit') !== null);
}

// Returns the table rows of a PR body that matches the fixed template, or null.
function parsePublicPrBody(text) {
    const lines = publicLines(text);
    const head = lines.slice(0, PUBLIC_PR_BODY_HEAD.length);
    const tail = lines.slice(lines.length - PUBLIC_PR_BODY_TAIL.length);
    const rows = lines.slice(PUBLIC_PR_BODY_HEAD.length, lines.length - PUBLIC_PR_BODY_TAIL.length);
    if (head.join('\n') !== PUBLIC_PR_BODY_HEAD.join('\n') || tail.join('\n') !== PUBLIC_PR_BODY_TAIL.join('\n')) {
        return null;
    }
    if (rows.length === 0 || rows.length > MAX_PUBLIC_ROWS) {
        return null;
    }
    const updates = rows.map(row => parsePublicUpdate(row, 'table'));
    return updates.every(Boolean) ? updates : null;
}

// Splits a `git format-patch` (`am` transport) file into its commit headers and messages.
// Each commit starts with an mbox separator and ends its message at the `---` line before
// the diffstat:
//   From 1234567890abcdef1234567890abcdef12345678 Mon Sep 17 00:00:00 2001
//   From: "github-actions[bot]" <github-actions[bot]@users.noreply.github.com>
//   Date: Sat, 3 Oct 2026 09:00:00 +0000
//   Subject: [PATCH 1/2] Update dependencies
//
//   lodash 4.17.20 -> 4.17.21
//   ---
//    extension/package-lock.json | 4 ++--
// Header continuation lines start with whitespace (RFC 5322 folding).
function parsePatchCommits(patchText) {
    const commits = [];
    let current = null;
    let inHeaders = false;
    for (const line of String(patchText ?? '').split('\n').map(text => text.replace(/\r$/, ''))) {
        if (/^From [0-9a-f]{40} Mon Sep 17 00:00:00 2001$/.test(line)) {
            current = { headers: [], message: [], closed: false };
            commits.push(current);
            inHeaders = true;
            continue;
        }
        if (!current || current.closed) {
            continue;
        }
        if (inHeaders) {
            if (line === '') {
                inHeaders = false;
            } else if (/^\s/.test(line) && current.headers.length > 0) {
                current.headers[current.headers.length - 1] += ` ${line.trim()}`;
            } else {
                current.headers.push(line);
            }
            continue;
        }
        if (line === '---') {
            current.closed = true;
            continue;
        }
        current.message.push(line);
    }
    return commits;
}

function checkPatchCommits(patchText) {
    const reasons = [];
    const commits = parsePatchCommits(patchText);
    if (commits.length === 0) {
        reasons.push('missing-commit-headers');
    }
    for (const commit of commits) {
        const headers = new Map();
        let unknownHeader = false;
        for (const header of commit.headers) {
            const match = /^([A-Za-z-]+): (.*)$/.exec(header);
            if (!match || headers.has(match[1].toLowerCase())) {
                unknownHeader = true;
                continue;
            }
            headers.set(match[1].toLowerCase(), match[2]);
        }
        // `git format-patch` writes only these headers for an ASCII commit; MIME headers mean
        // non-ASCII text, which the templates never contain.
        if (unknownHeader || [...headers.keys()].some(name => !['from', 'date', 'subject'].includes(name))) {
            reasons.push('unexpected-commit-header');
            continue;
        }
        const identity = String(headers.get('from') ?? '').replace(/^"([^"]*)"/, '$1');
        if (identity !== PUBLIC_COMMIT_IDENTITY) {
            reasons.push('unexpected-commit-author');
        }
        const subject = String(headers.get('subject') ?? '').replace(/^\[PATCH(?: \d+\/\d+)?\] /, '');
        // The blank line between subject and body is the header terminator, so restore it.
        if (!checkPublicCommitMessage([subject, '', ...commit.message].join('\n'))) {
            reasons.push('non-template-commit-message');
        }
    }
    return reasons;
}

/**
 * Checks every agent-authored string the create and push handlers publish: the PR title and
 * body, the push `message`, and each commit's author and message in the `am` patches. Every
 * PR body row must also name a package and target version that the patches add, so the
 * table cannot carry anything but the upgrade it describes. Returns `{ source, reason }`
 * violations.
 */
function checkPublicText(items, patches) {
    const violations = [];
    // A lockfile hunk often names the package only in a context line (for example
    // `"node_modules/lodash": {`), so the name may appear anywhere in the patch; the target
    // version must be on an added line.
    const patchText = [...patches.values()].join('\n').toLowerCase();
    const addedText = [...patches.values()].flatMap(text => parsePatchFileDiffs(text).flatMap(diff => diff.blocks.flatMap(block => block.added))).join('\n');
    for (const item of items) {
        if (item?.type === 'create_pull_request') {
            const title = String(item.title ?? '').trim();
            if (title !== PUBLIC_PR_TITLE && title !== `[auto-sec] ${PUBLIC_PR_TITLE}`) {
                violations.push({ source: 'create_pull_request.title', reason: 'non-template-text' });
            }
            const rows = parsePublicPrBody(item.body);
            if (rows === null) {
                violations.push({ source: 'create_pull_request.body', reason: 'non-template-text' });
            } else if (!rows.every(row => patchText.includes(row.name.toLowerCase()) && addedText.includes(row.to.replace(/^(?:[~^=]|[<>]=?)?v?/, '')))) {
                violations.push({ source: 'create_pull_request.body', reason: 'row-not-in-patch' });
            }
        } else if (item?.type === 'push_to_pull_request_branch' && !checkPublicCommitMessage(item.message)) {
            violations.push({ source: 'push_to_pull_request_branch.message', reason: 'non-template-text' });
        }
    }
    for (const [name, text] of patches) {
        violations.push(...checkPatchCommits(text).map(reason => ({ source: name, reason })));
    }
    return violations;
}

/**
 * Runs in the safe_outputs job before the create and push handlers and fails the job when
 * any published string does not match its fixed template (see `checkPublicText`). The
 * failure message names only the field and reason, never the rejected text.
 */
async function runPublicTextGate({ core, fs = require('node:fs'), env = process.env, patchDir = '/tmp/gh-aw' }) {
    const path = require('node:path');
    const outputPath = env.GH_AW_AGENT_OUTPUT;
    const agentOutput = outputPath && fs.existsSync(outputPath) ? JSON.parse(fs.readFileSync(outputPath, 'utf8')) : {};
    const items = (Array.isArray(agentOutput?.items) ? agentOutput.items : []).filter(item => ['create_pull_request', 'push_to_pull_request_branch'].includes(item?.type));
    const names = fs.existsSync(patchDir) ? fs.readdirSync(patchDir).filter(name => /^aw-.*\.patch$/.test(name)).sort() : [];
    const patches = new Map(names.map(name => [name, fs.readFileSync(path.join(patchDir, name), 'utf8')]));
    const violations = checkPublicText(items, patches);
    if (violations.length > 0) {
        core.setFailed(`auto-sec public text gate failed: ${violations.map(v => `${v.source} ${v.reason}`).join('; ')}`);
    } else {
        core.info(`Public text gate passed for ${items.length} output(s) and ${names.length} patch file(s).`);
    }
    return { outputs: items.length, patches: names.length, violations };
}

// The blocked reason codes the prompt defines. The run summary is public, so the noop
// report may carry only these codes and counts.
const PUBLIC_BLOCKED_REASONS = new Set([
    'actions-pin-requires-maintainer',
    'auto-sec-pr-conflict',
    'breaking-upgrade-required',
    'no-safe-version',
    'no-version-past-cooldown',
    'nuget-not-mirrored',
    'nuget-version-managed',
    'source-build-required',
    'unsupported-ecosystem',
    'update-failed',
]);

// A noop report is counts only, for example:
//   alerts=12 dependabot-pr=3 auto-sec-pr=6 blocked=3 (nuget-not-mirrored=1, breaking-upgrade-required=2) code-findings-out-of-scope=344
// The parenthesized breakdown is omitted when nothing is blocked.
function checkPublicNoopMessage(text) {
    const match = /^alerts=\d{1,6} dependabot-pr=\d{1,6} auto-sec-pr=\d{1,6} blocked=\d{1,6}(?: \(([a-z-]+=\d{1,6}(?:, [a-z-]+=\d{1,6})*)\))? code-findings-out-of-scope=\d{1,6}$/.exec(String(text ?? '').trim());
    if (!match) {
        return false;
    }
    const codes = match[1] ? match[1].split(', ').map(pair => pair.split('=')[0]) : [];
    return codes.every(code => PUBLIC_BLOCKED_REASONS.has(code)) && new Set(codes).size === codes.length;
}

// Safe-output types whose agent-authored text the conclusion job would publish in issues
// (`missing_tool`, `missing_data`, `report_incomplete`) have no fixed template, so only
// these types survive the scrub.
const PUBLIC_OUTPUT_TYPES = new Set(['create_pull_request', 'push_to_pull_request_branch', 'approve_dependabot_pr', 'noop']);

// Files under /tmp/gh-aw that hold the agent transcript (prompt responses, tool calls, and
// tool output such as the private alert inputs) or agent-written summaries. The agent job
// uploads /tmp/gh-aw as the public `agent` artifact and renders these logs into the public
// run summary, so they are removed before either happens.
const AGENT_TRANSCRIPT_PATHS = [
    'sandbox/agent/logs',
    'mcp-logs',
    'proxy-logs',
    'agent-stdio.log',
    'agent-step-summary.md',
    'redacted-urls.log',
    'otel.jsonl',
    'otlp-export-errors.jsonl',
];

/**
 * Checks the raw safe-output items before they leave the agent job: every item must be a
 * template-checked type, a `noop` must be counts only, an approval request is reduced to
 * a valid PR number and head SHA, and PR text and patches must pass `checkPublicText`.
 * Patch contents must also pass `checkPatchUploadSafety`, since the patches are uploaded
 * before the safe_outputs job's content gate runs. Returns the items to keep and
 * `{ source, reason }` violations.
 */
function checkAgentOutputs(lines, patches) {
    const violations = [];
    const kept = [];
    for (const [index, line] of lines.entries()) {
        let item;
        try {
            item = JSON.parse(line);
        } catch {
            violations.push({ source: `line ${index + 1}`, reason: 'malformed-output' });
            continue;
        }
        if (!PUBLIC_OUTPUT_TYPES.has(item?.type)) {
            continue;
        }
        if (item.type === 'noop' && !checkPublicNoopMessage(item.message)) {
            violations.push({ source: 'noop.message', reason: 'non-template-text' });
        } else if (item.type === 'approve_dependabot_pr') {
            // The approval job reads only these two inputs, so anything else the item
            // carries is dropped rather than uploaded.
            if (!/^[1-9]\d{0,9}$/.test(String(item.pr_number ?? '')) || !/^[0-9a-f]{40}$/.test(String(item.head_sha ?? ''))) {
                violations.push({ source: 'approve_dependabot_pr', reason: 'invalid-inputs' });
            }
            kept.push({ type: item.type, pr_number: item.pr_number, head_sha: item.head_sha });
            continue;
        }
        kept.push(item);
    }
    violations.push(...checkPublicText(kept, patches));
    for (const [name, text] of patches) {
        violations.push(...checkPatchUploadSafety(text).map(reason => ({ source: name, reason })));
    }
    return { kept, violations };
}

// The manifest and lockfile basenames the create/push `allowed-files` lists permit.
const UPLOADABLE_PATCH_BASENAMES = new Set([
    'package.json',
    'package-lock.json',
    'npm-shrinkwrap.json',
    'yarn.lock',
    'pnpm-lock.yaml',
    'uv.lock',
    'pyproject.toml',
    'Directory.Packages.props',
]);

/**
 * Base-free checks on an agent patch before the agent job uploads it in the public `agent`
 * artifact. The full content gate needs the original files and runs later in the
 * safe_outputs job, so without this a patch the handlers would reject (for example one
 * adding `.auto-sec/alerts.json`, or advisory text in any line) would still be published
 * in the artifact. Every file diff must be an in-place edit of an allowed manifest or
 * lockfile, no line anywhere in the patch may carry advisory text, and lockfiles may not
 * gain comments. Returns reason codes.
 */
function checkPatchUploadSafety(patchText) {
    const reasons = new Set();
    for (const diff of parsePatchFileDiffs(patchText)) {
        if (!diff.parseable || diff.oldPath !== diff.newPath || diff.metadata.length > 0
            || String(diff.newPath).startsWith('.github/')
            || !UPLOADABLE_PATCH_BASENAMES.has(basenameOf(diff.newPath ?? ''))) {
            reasons.add('disallowed-patch-file');
        }
        if (COMMENT_LOCKFILE_BASENAMES.has(basenameOf(diff.newPath ?? '').toLowerCase())
            && diff.blocks.some(block => block.added.some(hasLockfileComment))) {
            reasons.add('lockfile-comment');
        }
    }
    // Context lines, diffstat lines, and any text between hunks are uploaded too, so the
    // whole patch is scanned rather than only the added lines.
    if (String(patchText ?? '').split('\n').some(line => PUBLIC_FORBIDDEN_TOKEN.test(line))) {
        reasons.add('forbidden-public-text');
    }
    return [...reasons];
}

/**
 * Runs in the agent job right after the built-in secret redaction, before the step
 * summaries, the safe-output ingestion, and the `agent` artifact upload. It deletes the
 * agent transcript, drops free-text output types, and validates the rest. When anything
 * is off-template, it empties the safe outputs and deletes the patches so no downstream
 * job, artifact, or summary carries the text, then fails the job naming only the field
 * and reason.
 */
async function runAgentOutputScrub({ core, fs = require('node:fs'), env = process.env, workDir = '/tmp/gh-aw' }) {
    const path = require('node:path');
    for (const relative of AGENT_TRANSCRIPT_PATHS) {
        fs.rmSync(path.join(workDir, relative), { recursive: true, force: true });
    }
    const outputPath = env.GH_AW_SAFE_OUTPUTS;
    const lines = outputPath && fs.existsSync(outputPath)
        ? fs.readFileSync(outputPath, 'utf8').split('\n').filter(line => line.trim() !== '')
        : [];
    const artifactNames = fs.existsSync(workDir) ? fs.readdirSync(workDir).filter(name => /^aw-.*\.(?:patch|bundle)$/.test(name)).sort() : [];
    const patchNames = artifactNames.filter(name => name.endsWith('.patch'));
    const patches = new Map(patchNames.map(name => [name, fs.readFileSync(path.join(workDir, name), 'utf8')]));
    const { kept, violations } = checkAgentOutputs(lines, patches);
    if (violations.length > 0) {
        if (outputPath && fs.existsSync(outputPath)) {
            fs.writeFileSync(outputPath, '');
        }
        for (const name of artifactNames) {
            fs.rmSync(path.join(workDir, name), { force: true });
        }
        core.setFailed(`auto-sec agent output scrub failed: ${violations.map(v => `${v.source} ${v.reason}`).join('; ')}`);
        return { kept: 0, dropped: lines.length, violations };
    }
    if (outputPath && fs.existsSync(outputPath)) {
        fs.writeFileSync(outputPath, kept.map(item => JSON.stringify(item)).join('\n') + (kept.length > 0 ? '\n' : ''));
    }
    core.info(`Agent output scrub kept ${kept.length} of ${lines.length} output(s).`);
    return { kept: kept.length, dropped: lines.length - kept.length, violations };
}

async function main(argv) {
    const [command, ecosystem, name, version] = argv;
    if (command !== 'lookup' || !ecosystem || !name || !version) {
        console.error('Usage: node auto-sec.js lookup <npm|pip|nuget> <package> <version>');
        process.exitCode = 2;
        return;
    }
    // The agent runs this CLI from its checkout, so the repository NuGet.config sits three
    // directories above this file and decides which feeds a NuGet package restores from.
    const nugetConfigPath = require('node:path').resolve(__dirname, '..', '..', '..', 'NuGet.config');
    const nugetConfigText = ecosystem === 'nuget' ? require('node:fs').readFileSync(nugetConfigPath, 'utf8') : null;
    console.log(JSON.stringify(await lookupPackageVersion(ecosystem, name, version, { nugetConfigText })));
}

if (require.main === module) {
    main(process.argv.slice(2)).catch(error => {
        console.error(error.message);
        process.exitCode = 1;
    });
}

module.exports = {
    APPROVED_SOURCE_PREFIXES,
    AUTO_SEC_BRANCH,
    COOLDOWN_DAYS,
    MAX_APPROVALS,
    MAX_VERSION_CHANGES,
    compareVersions,
    coveredAlerts,
    ecosystemFromBranch,
    evaluateApprovalGates,
    extractSources,
    findNewSources,
    inVulnerableRanges,
    isAllowedManifest,
    isVersionOnlyEdit,
    unboundLockfileArtifacts,
    isBreakingChange,
    isCooldownSatisfied,
    lookupPackageVersion,
    manifestMentionsVersion,
    manifestPackageVersions,
    manifestVersionChanges,
    normalizePackageName,
    parseDependabotUpdates,
    parseNuGetConfig,
    readApprovalRequests,
    runAgentOutputScrub,
    runApprovalJob,
    runPatchContentGate,
    runPublicTextGate,
    runPushTargetGate,
    checkAgentOutputs,
    checkPatchContents,
    checkPatchUploadSafety,
    checkPatchVersionPolicy,
    checkPublicNoopMessage,
    checkPublicText,
    selectNuGetSources,
};
