// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Restores MSBuild version properties after `dotnet outdated -u` updates
// Directory.Packages.props.
//
//   dotnet run .github/workflows/update-dependencies-version-properties.cs -- <original-Directory.Packages.props>
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
// eng/Versions.props, so inlining the version would leave them behind. This app
// puts the property reference back and moves the new version into eng/Versions.props.
//
// Only packages in the first <ItemGroup> (the external dependencies that the
// update-dependencies workflow owns) get new versions. Every other inlined
// reference, such as the manual .NET 10 servicing pins or a package that Darc
// manages through eng/Version.Details.xml, is restored without changing its
// version, because those versions flow through other processes.
//
// The files are edited as text rather than loaded with an XML API so that
// comments, whitespace, and attribute order are preserved exactly.

using System.Text.RegularExpressions;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: dotnet run update-dependencies-version-properties.cs -- <original-Directory.Packages.props>");
    return 1;
}

const string PackagesPropsPath = "Directory.Packages.props";
var versionsPropsPath = Path.Combine("eng", "Versions.props");
var versionDetailsPath = Path.Combine("eng", "Version.Details.xml");

var originalPackages = PackageVersion.ReadAll(File.ReadAllText(args[0]));
var updatedPackagesProps = File.ReadAllText(PackagesPropsPath);
var versionsProps = File.ReadAllText(versionsPropsPath);
var versionDetailsXml = File.ReadAllText(versionDetailsPath);

var propertyByKey = new Dictionary<string, string>(StringComparer.Ordinal);
foreach (var package in originalPackages)
{
    if (PackageVersion.ReadPropertyReference(package.Version) is { } property)
    {
        propertyByKey[package.Key] = property;
    }
}

// Collect every inlined property reference before editing so that packages
// sharing a property can be checked for agreement.
var inlined = PackageVersion.ReadAll(updatedPackagesProps)
    .Where(p => propertyByKey.TryGetValue(p.Key, out var property) && p.Version != $"$({property})")
    .Select(p => (Package: p, Property: propertyByKey[p.Key]))
    .ToList();

var versionsByProperty = inlined
    .GroupBy(i => i.Property)
    .ToDictionary(g => g.Key, g => g.Select(i => i.Package.Version).Distinct().Order(StringComparer.Ordinal).ToList());

var packagesProps = updatedPackagesProps;
var updated = new List<string>();
var skipped = new List<string>();

// Edit from the end of the file so earlier offsets stay valid.
foreach (var (package, property) in inlined.OrderByDescending(i => i.Package.Index))
{
    var restoredElement = Regex.Replace(package.Element, @"\bVersion=""[^""]*""", $"Version=\"$({property})\"");
    packagesProps = string.Concat(packagesProps.AsSpan(0, package.Index), restoredElement, packagesProps.AsSpan(package.Index + package.Element.Length));

    var propertyElement = new Regex($"<{Regex.Escape(property)}>[^<]*</{Regex.Escape(property)}>");
    var versions = versionsByProperty[property];
    var reason =
        !package.IsExternalDependency ? "the package is outside the external dependencies ItemGroup" :
        IsDarcDependency(versionDetailsXml, package.Id) ? "the package is managed by Darc in eng/Version.Details.xml" :
        !propertyElement.IsMatch(versionsProps) ? "the property is not defined in eng/Versions.props" :
        versions.Count > 1 ? $"packages sharing the property resolved to different versions ({string.Join(", ", versions)})" :
        null;

    if (reason is null)
    {
        versionsProps = propertyElement.Replace(versionsProps, $"<{property}>{package.Version}</{property}>");
        updated.Add($"Moved {package.Id} {package.Version} into $({property}) in eng/Versions.props.");
    }
    else
    {
        skipped.Add($"::warning::Kept $({property}) unchanged instead of updating {package.Id} to {package.Version}: {reason}.");
    }
}

File.WriteAllText(PackagesPropsPath, packagesProps);
File.WriteAllText(versionsPropsPath, versionsProps);

foreach (var message in updated.Order(StringComparer.Ordinal).Concat(skipped.Order(StringComparer.Ordinal)))
{
    Console.WriteLine(message);
}

return 0;

static bool IsDarcDependency(string versionDetailsXml, string packageId) =>
    Regex.IsMatch(versionDetailsXml, $@"<Dependency\s+Name=""{Regex.Escape(packageId)}""", RegexOptions.IgnoreCase);

// A <PackageVersion Include="..." Version="..." /> element and its offset in the file. Key pairs the
// element with the same element in another copy of the file: the package ID plus its occurrence
// number, because a package can appear in several ItemGroups (for example, a .NET 10 pin and a
// .NET 11 override).
internal sealed partial record PackageVersion(string Id, string Version, string Key, string Element, int Index, bool IsExternalDependency)
{
    public static List<PackageVersion> ReadAll(string content)
    {
        // The first unconditional <ItemGroup> holds the external dependencies.
        var groupStart = content.IndexOf("<ItemGroup>", StringComparison.Ordinal);
        var groupEnd = groupStart < 0 ? -1 : content.IndexOf("</ItemGroup>", groupStart, StringComparison.Ordinal);
        if (groupEnd < 0)
        {
            throw new InvalidOperationException("Directory.Packages.props has no complete unconditional <ItemGroup>.");
        }

        var occurrences = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var packages = new List<PackageVersion>();
        foreach (Match match in PackageVersionElement().Matches(content))
        {
            var id = IncludeAttribute().Match(match.Value);
            var version = VersionAttribute().Match(match.Value);
            if (!id.Success || !version.Success)
            {
                continue;
            }

            var occurrence = occurrences.GetValueOrDefault(id.Groups[1].Value);
            occurrences[id.Groups[1].Value] = occurrence + 1;
            packages.Add(new PackageVersion(
                id.Groups[1].Value,
                version.Groups[1].Value,
                $"{id.Groups[1].Value.ToLowerInvariant()}#{occurrence}",
                match.Value,
                match.Index,
                match.Index > groupStart && match.Index < groupEnd));
        }

        return packages;
    }

    public static string? ReadPropertyReference(string version) =>
        SinglePropertyReference().Match(version) is { Success: true } match ? match.Groups[1].Value : null;

    [GeneratedRegex(@"<PackageVersion\b[^>]*>")]
    private static partial Regex PackageVersionElement();

    [GeneratedRegex(@"\bInclude=""([^""]*)""")]
    private static partial Regex IncludeAttribute();

    [GeneratedRegex(@"\bVersion=""([^""]*)""")]
    private static partial Regex VersionAttribute();

    // Matches a Version attribute value that is exactly one MSBuild property reference and captures
    // the property name: "$(OpenTelemetryInstrumentationHttpVersion)" -> OpenTelemetryInstrumentationHttpVersion.
    // Literal versions ("1.21.0") and expressions ("$(A)-$(B)") don't match, so they are left alone.
    [GeneratedRegex(@"^\$\(([A-Za-z_][A-Za-z0-9_.-]*)\)$")]
    private static partial Regex SinglePropertyReference();
}
