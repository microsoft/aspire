'use strict';

// Keeps the actionlint pin in .github/actionlint-version.json current.
//
//   node update-actionlint.js check
//     Compares the latest published rhysd/actionlint release with the pin. When
//     newer, downloads the linux_amd64 archive into RUNNER_TEMP, verifies it
//     against the release API asset digest, and emits step outputs. Never edits
//     the repository.
//
//   node update-actionlint.js apply <version> <sha256>
//     Rewrites the pin file with the verified version and SHA-256.
//
// The release API digest comes from the same upstream as the archive, so it is
// not an independent trust anchor. Its purpose is to make the reviewed,
// committed hash match the bytes the release served, so CI detects any later
// substitution of the archive.

const crypto = require('node:crypto');
const fs = require('node:fs/promises');
const path = require('node:path');

const REPOSITORY = 'rhysd/actionlint';
const LATEST_RELEASE_URL = `https://api.github.com/repos/${REPOSITORY}/releases/latest`;
const SEMVER = /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/;
const SHA256 = /^[0-9a-f]{64}$/;

function parseVersion(value, description) {
    const match = typeof value === 'string' ? SEMVER.exec(value) : null;
    if (!match) {
        throw new Error(`${description} '${value}' is not a MAJOR.MINOR.PATCH version.`);
    }

    return match.slice(1).map(Number);
}

function compareVersions(left, right) {
    const a = parseVersion(left, 'Version');
    const b = parseVersion(right, 'Version');
    for (let i = 0; i < 3; i++) {
        if (a[i] !== b[i]) {
            return a[i] < b[i] ? -1 : 1;
        }
    }

    return 0;
}

function parseReleaseTag(tag) {
    if (typeof tag !== 'string' || !tag.startsWith('v') || !SEMVER.test(tag.slice(1))) {
        throw new Error(`Release tag '${tag}' is not of the form vMAJOR.MINOR.PATCH.`);
    }

    return tag.slice(1);
}

// Pin file shape (.github/actionlint-version.json):
//   { "version": "1.7.12", "linuxAmd64Sha256": "8aca8db9..." }
function readPin(content) {
    let pin;
    try {
        pin = JSON.parse(content);
    } catch (error) {
        throw new Error(`Pin file is not valid JSON: ${error.message}`);
    }

    const keys = Object.keys(pin ?? {}).sort().join(',');
    if (keys !== 'linuxAmd64Sha256,version') {
        throw new Error(`Pin file must contain exactly 'version' and 'linuxAmd64Sha256' but has '${keys}'.`);
    }

    parseVersion(pin.version, 'Pinned version');
    if (!SHA256.test(pin.linuxAmd64Sha256)) {
        throw new Error(`Pinned linuxAmd64Sha256 '${pin.linuxAmd64Sha256}' is not a lowercase SHA-256 hex digest.`);
    }

    return { version: pin.version, sha256: pin.linuxAmd64Sha256 };
}

function applyPin(content, version, sha256) {
    const current = readPin(content);
    if (compareVersions(version, current.version) <= 0) {
        throw new Error(`Refusing to change actionlint from ${current.version} to ${version}; only upgrades are allowed.`);
    }

    if (!SHA256.test(sha256)) {
        throw new Error(`SHA-256 '${sha256}' is not a lowercase SHA-256 hex digest.`);
    }

    return `${JSON.stringify({ version, linuxAmd64Sha256: sha256 }, null, 2)}\n`;
}

// Release API asset shape (https://docs.github.com/rest/releases/releases#get-the-latest-release):
//   { "name": "actionlint_1.7.12_linux_amd64.tar.gz",
//     "browser_download_url": "https://github.com/rhysd/actionlint/releases/download/v1.7.12/actionlint_1.7.12_linux_amd64.tar.gz",
//     "digest": "sha256:8aca8db9..." }
// Older releases can have a null digest; those are rejected rather than trusted.
function selectLinuxAmd64Asset(release, version) {
    if (release.draft || release.prerelease) {
        throw new Error(`Release ${release.tag_name} is a draft or prerelease.`);
    }

    const name = `actionlint_${version}_linux_amd64.tar.gz`;
    const assets = (release.assets ?? []).filter(asset => asset.name === name);
    if (assets.length !== 1) {
        throw new Error(`Expected exactly one '${name}' asset in release ${release.tag_name} but found ${assets.length}.`);
    }

    const [asset] = assets;
    const url = `https://github.com/${REPOSITORY}/releases/download/v${version}/${name}`;
    if (asset.browser_download_url !== url) {
        throw new Error(`Asset '${name}' has unexpected download URL '${asset.browser_download_url}'.`);
    }

    const digest = /^sha256:([0-9a-f]{64})$/.exec(asset.digest ?? '');
    if (!digest) {
        throw new Error(`Asset '${name}' has no sha256 digest (got '${asset.digest}').`);
    }

    return { name, url, sha256: digest[1] };
}

function verifySha256(bytes, expected, name) {
    const actual = crypto.createHash('sha256').update(bytes).digest('hex');
    if (actual !== expected) {
        throw new Error(`Downloaded '${name}' has SHA-256 ${actual} but the release API digest is ${expected}.`);
    }
}

async function check({ pinContent, outputDir, fetchJson, fetchBytes, writeFile, log }) {
    const current = readPin(pinContent);
    const release = await fetchJson(LATEST_RELEASE_URL);
    const latest = parseReleaseTag(release.tag_name);
    const comparison = compareVersions(latest, current.version);

    if (comparison < 0) {
        throw new Error(`Latest actionlint release ${latest} is older than the pinned ${current.version}; refusing to downgrade.`);
    }

    if (comparison === 0) {
        log(`actionlint ${current.version} is the latest release; nothing to update.`);
        return { updated: false, previousVersion: current.version };
    }

    const asset = selectLinuxAmd64Asset(release, latest);
    const bytes = await fetchBytes(asset.url);
    verifySha256(bytes, asset.sha256, asset.name);

    const archivePath = path.join(outputDir, asset.name);
    await writeFile(archivePath, bytes);
    log(`actionlint ${latest} is available (pinned ${current.version}); verified ${asset.name} SHA-256 ${asset.sha256}.`);

    return { updated: true, previousVersion: current.version, version: latest, sha256: asset.sha256, archivePath };
}

// Formats check() results as GITHUB_OUTPUT lines, e.g.:
//   updated=true
//   previous-version=1.7.12
//   version=1.7.13
//   sha256=<64 hex chars>
//   archive-path=/home/runner/work/_temp/actionlint_1.7.13_linux_amd64.tar.gz
// update-actionlint.yml consumes these names, so keep them in sync.
function formatStepOutputs(result) {
    const lines = [`updated=${result.updated}`, `previous-version=${result.previousVersion}`];
    if (result.updated) {
        lines.push(`version=${result.version}`, `sha256=${result.sha256}`, `archive-path=${result.archivePath}`);
    }

    return `${lines.join('\n')}\n`;
}

async function main(argv, env) {
    const pinPath = path.resolve('.github/actionlint-version.json');
    const pinContent = await fs.readFile(pinPath, 'utf8');
    const [command, ...args] = argv;

    if (command === 'apply') {
        const [version, sha256] = args;
        await fs.writeFile(pinPath, applyPin(pinContent, version, sha256));
        console.log(`Updated ${pinPath} to actionlint ${version}.`);
        return;
    }

    if (command !== 'check') {
        throw new Error(`Unknown command '${command}'. Expected 'check' or 'apply <version> <sha256>'.`);
    }

    const apiHeaders = {
        Accept: 'application/vnd.github+json',
        'X-GitHub-Api-Version': '2022-11-28',
        ...(env.GITHUB_TOKEN ? { Authorization: `Bearer ${env.GITHUB_TOKEN}` } : {}),
    };

    const result = await check({
        pinContent,
        outputDir: env.RUNNER_TEMP ?? process.cwd(),
        fetchJson: async url => {
            const response = await fetch(url, { headers: apiHeaders });
            if (!response.ok) {
                throw new Error(`GET ${url} failed with HTTP ${response.status}.`);
            }

            return response.json();
        },
        // The download redirects to a release-asset CDN, so it is fetched without the API token.
        fetchBytes: async url => {
            const response = await fetch(url);
            if (!response.ok) {
                throw new Error(`GET ${url} failed with HTTP ${response.status}.`);
            }

            return Buffer.from(await response.arrayBuffer());
        },
        writeFile: fs.writeFile,
        log: message => console.log(message),
    });

    if (env.GITHUB_OUTPUT) {
        await fs.appendFile(env.GITHUB_OUTPUT, formatStepOutputs(result));
    }
}

module.exports = { applyPin, check, compareVersions, formatStepOutputs, parseReleaseTag, readPin, selectLinuxAmd64Asset, verifySha256 };

if (require.main === module) {
    main(process.argv.slice(2), process.env).catch(error => {
        console.error(`::error::${error.message}`);
        process.exitCode = 1;
    });
}
