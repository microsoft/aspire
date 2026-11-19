'use strict';

// Restores MSBuild version properties after `dotnet outdated -u` updates
// Directory.Packages.props.
//
//   node update-dependencies-version-properties.js <original-Directory.Packages.props>
//
// Run from the repository root after `dotnet outdated`. The argument is a copy of
// Directory.Packages.props from before the update (for example, the output of
// `git show HEAD:Directory.Packages.props`).
//
// dotnet-outdated writes the new version directly into the Version attribute, even
// when it was a property reference:
//
//   before: <PackageVersion Include="OpenTelemetry.Extensions.Hosting" Version="$(OpenTelemetryInstrumentationExtensionsHostingVersion)" />
//   after:  <PackageVersion Include="OpenTelemetry.Extensions.Hosting" Version="1.21.0" />
//
// Other files, such as the project templates, read those properties from
// eng/Versions.props, so inlining the version would leave them behind. This script
// puts the property reference back and moves the new version into eng/Versions.props.
//
// Only packages in the first <ItemGroup> (the external dependencies that the
// update-dependencies workflow owns) get new versions. Every other inlined
// reference, such as the manual .NET 10 servicing pins or a package that Darc
// manages through eng/Version.Details.xml, is restored without changing its
// version, because those versions flow through other processes.

const fs = require('node:fs/promises');

const PACKAGE_VERSION_ELEMENT = /<PackageVersion\b[^>]*>/g;
const PROPERTY_REFERENCE = /^\$\(([A-Za-z_][A-Za-z0-9_.-]*)\)$/;

function escapeRegExp(value) {
    return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

function readAttribute(element, name) {
    const match = new RegExp(`\\b${name}="([^"]*)"`).exec(element);
    return match ? match[1] : null;
}

// Returns the [start, end) offsets of the first unconditional <ItemGroup>, which
// holds the external dependencies in Directory.Packages.props.
function findFirstItemGroup(content) {
    const start = content.search(/<ItemGroup>/);
    if (start < 0) {
        throw new Error('Directory.Packages.props has no unconditional <ItemGroup>.');
    }

    const end = content.indexOf('</ItemGroup>', start);
    if (end < 0) {
        throw new Error('Directory.Packages.props has an unterminated <ItemGroup>.');
    }

    return [start, end];
}

// Lists the PackageVersion Include elements with their offsets. Each entry gets a
// key that pairs it with the same element in another copy of the file: the
// package ID plus its occurrence number, because a package can appear in several
// ItemGroups (for example, a .NET 10 pin and a .NET 11 override).
function readPackageVersions(content) {
    const [groupStart, groupEnd] = findFirstItemGroup(content);
    const occurrences = new Map();
    const packages = [];
    for (const match of content.matchAll(PACKAGE_VERSION_ELEMENT)) {
        const id = readAttribute(match[0], 'Include');
        const version = readAttribute(match[0], 'Version');
        if (id === null || version === null) {
            continue;
        }

        const occurrence = occurrences.get(id.toLowerCase()) ?? 0;
        occurrences.set(id.toLowerCase(), occurrence + 1);
        packages.push({
            id,
            version,
            key: `${id.toLowerCase()}#${occurrence}`,
            element: match[0],
            index: match.index,
            isExternalDependency: match.index > groupStart && match.index < groupEnd,
        });
    }

    return packages;
}

function isDarcDependency(versionDetailsXml, packageId) {
    return new RegExp(`<Dependency\\s+Name="${escapeRegExp(packageId)}"`, 'i').test(versionDetailsXml);
}

function restoreVersionProperties({ originalPackagesProps, updatedPackagesProps, versionsProps, versionDetailsXml }) {
    const propertyByKey = new Map();
    for (const { key, version } of readPackageVersions(originalPackagesProps)) {
        const reference = PROPERTY_REFERENCE.exec(version);
        if (reference) {
            propertyByKey.set(key, reference[1]);
        }
    }

    // Collect every inlined property reference before editing so that packages
    // sharing a property can be checked for agreement.
    const inlined = [];
    for (const entry of readPackageVersions(updatedPackagesProps)) {
        const property = propertyByKey.get(entry.key);
        if (property && entry.version !== `$(${property})`) {
            inlined.push({ ...entry, property });
        }
    }

    const versionsByProperty = new Map();
    for (const { property, version } of inlined) {
        versionsByProperty.set(property, new Set([...(versionsByProperty.get(property) ?? []), version]));
    }

    const updated = [];
    const skipped = [];
    let packagesProps = updatedPackagesProps;
    let newVersionsProps = versionsProps;

    // Edit from the end of the file so earlier offsets stay valid.
    for (const entry of [...inlined].sort((a, b) => b.index - a.index)) {
        const restoredElement = entry.element.replace(/\bVersion="[^"]*"/, `Version="$(${entry.property})"`);
        packagesProps = packagesProps.slice(0, entry.index) + restoredElement + packagesProps.slice(entry.index + entry.element.length);

        const propertyPattern = `<${escapeRegExp(entry.property)}>[^<]*</${escapeRegExp(entry.property)}>`;
        const versions = versionsByProperty.get(entry.property);
        const result = { packageId: entry.id, property: entry.property, version: entry.version };
        if (!entry.isExternalDependency) {
            skipped.push({ ...result, reason: 'the package is outside the external dependencies ItemGroup' });
        } else if (isDarcDependency(versionDetailsXml, entry.id)) {
            skipped.push({ ...result, reason: 'the package is managed by Darc in eng/Version.Details.xml' });
        } else if (!new RegExp(propertyPattern).test(newVersionsProps)) {
            skipped.push({ ...result, reason: 'the property is not defined in eng/Versions.props' });
        } else if (versions.size > 1) {
            skipped.push({ ...result, reason: `packages sharing the property resolved to different versions (${[...versions].sort().join(', ')})` });
        } else {
            newVersionsProps = newVersionsProps.replace(new RegExp(propertyPattern, 'g'), `<${entry.property}>${entry.version}</${entry.property}>`);
            updated.push(result);
        }
    }

    const byPackage = (a, b) => a.packageId.localeCompare(b.packageId);
    return { packagesProps, versionsProps: newVersionsProps, updated: updated.sort(byPackage), skipped: skipped.sort(byPackage) };
}

async function main(args) {
    if (args.length !== 1) {
        throw new Error('Usage: node update-dependencies-version-properties.js <original-Directory.Packages.props>');
    }

    const result = restoreVersionProperties({
        originalPackagesProps: await fs.readFile(args[0], 'utf8'),
        updatedPackagesProps: await fs.readFile('Directory.Packages.props', 'utf8'),
        versionsProps: await fs.readFile('eng/Versions.props', 'utf8'),
        versionDetailsXml: await fs.readFile('eng/Version.Details.xml', 'utf8'),
    });

    await fs.writeFile('Directory.Packages.props', result.packagesProps);
    await fs.writeFile('eng/Versions.props', result.versionsProps);

    for (const { packageId, property, version } of result.updated) {
        console.log(`Moved ${packageId} ${version} into $(${property}) in eng/Versions.props.`);
    }

    for (const { packageId, property, version, reason } of result.skipped) {
        console.log(`::warning::Kept $(${property}) unchanged instead of updating ${packageId} to ${version}: ${reason}.`);
    }
}

module.exports = { restoreVersionProperties };

if (require.main === module) {
    main(process.argv.slice(2)).catch(error => {
        console.error(`::error::${error.message}`);
        process.exitCode = 1;
    });
}
