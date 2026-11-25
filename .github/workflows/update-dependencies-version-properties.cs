// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Moves version updates that `dotnet outdated -u` makes in Directory.Packages.props
// into the eng/Versions.props properties that own them.
//
//   dotnet run .github/workflows/update-dependencies-version-properties.cs -- <original-Directory.Packages.props>
//
// Run from the repository root after `dotnet outdated`. The argument is a copy of
// Directory.Packages.props from before the update (for example, the output of
// `git show HEAD:Directory.Packages.props`).
//
// Many PackageVersion entries use a property that eng/Versions.props defines, because
// other files (such as the project templates) read the same property:
//
//   <PackageVersion Include="OpenTelemetry.Extensions.Hosting" Version="$(OpenTelemetryInstrumentationExtensionsHostingVersion)" />
//
// dotnet-outdated doesn't update eng/Versions.props (Directory.Packages.props imports it
// conditionally). Depending on the dotnet-outdated version, it records the new version in
// Directory.Packages.props in one of these shapes:
//
//   - Inlined into the Version attribute (versions before 4.9.0):
//       <PackageVersion Include="OpenTelemetry.Extensions.Hosting" Version="1.21.0" />
//
//   - Added as an overriding property in Directory.Packages.props's own PropertyGroup
//     (4.9.0). The same run can also repoint a reference to another property that
//     resolved to the new version, for example the .NET 10 pin
//     Version="$(SystemTextJsonNet10Version)" becomes Version="$(SystemTextJsonVersion)":
//       <OpenTelemetryInstrumentationExtensionsHostingVersion>1.21.0</OpenTelemetryInstrumentationExtensionsHostingVersion>
//
// Either way, eng/Versions.props would be bypassed. This app restores every original
// property reference, removes the added overriding properties, and moves the new version
// into eng/Versions.props.
//
// Only properties used exclusively by packages in the first <ItemGroup> (the external
// dependencies that the update-dependencies workflow owns) get new versions. Other
// properties, such as the manual .NET 10 servicing pins or those for packages that Darc
// manages through eng/Version.Details.xml, keep their versions because those flow
// through other processes. Each skipped update is logged as a workflow warning.
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

var originalPackagesProps = File.ReadAllText(args[0]);
var packagesProps = File.ReadAllText(PackagesPropsPath);
var versionsProps = File.ReadAllText(versionsPropsPath);
var versionDetailsXml = File.ReadAllText(versionDetailsPath);

var originalPackages = PackageVersion.ReadAll(originalPackagesProps);
var propertyByKey = new Dictionary<string, string>(StringComparer.Ordinal);
foreach (var package in originalPackages)
{
    if (PackageVersion.ReadPropertyReference(package.Version) is { } property)
    {
        propertyByKey[package.Key] = property;
    }
}

// The new versions that dotnet-outdated chose for each property, in either output shape.
var candidates = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
void AddCandidate(string property, string version)
{
    if (!candidates.TryGetValue(property, out var versions))
    {
        candidates[property] = versions = new SortedSet<string>(StringComparer.Ordinal);
    }

    versions.Add(version);
}

// Restore every changed property reference. Edit from the end of the file so earlier
// offsets stay valid.
foreach (var package in PackageVersion.ReadAll(packagesProps).OrderByDescending(p => p.Index))
{
    if (!propertyByKey.TryGetValue(package.Key, out var property) || package.Version == $"$({property})")
    {
        continue;
    }

    // A literal version is the new value. A repointed reference carries no value of its
    // own; its overriding property is handled below.
    if (!package.Version.Contains("$(", StringComparison.Ordinal))
    {
        AddCandidate(property, package.Version);
    }

    var restoredElement = PackageVersion.ReplaceVersion(package.Element, $"$({property})");
    packagesProps = string.Concat(packagesProps.AsSpan(0, package.Index), restoredElement, packagesProps.AsSpan(package.Index + package.Element.Length));
}

// Remove properties that dotnet-outdated added to Directory.Packages.props to override
// eng/Versions.props. A property that Directory.Packages.props already defined (such as
// the FluentUI version) is updated in place by dotnet-outdated and left alone.
var originalDefinitions = Property.ReadAll(originalPackagesProps).Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
var referencedProperties = propertyByKey.Values.ToHashSet(StringComparer.Ordinal);
foreach (var added in Property.ReadAll(packagesProps).Where(p => !originalDefinitions.Contains(p.Name)).Reverse())
{
    if (!referencedProperties.Contains(added.Name) && !Property.IsDefined(versionsProps, added.Name))
    {
        continue;
    }

    AddCandidate(added.Name, added.Value);
    packagesProps = packagesProps.Remove(added.LineStart, added.LineLength);
}

var updated = new List<string>();
var skipped = new List<string>();
foreach (var (property, versions) in candidates.OrderBy(c => c.Key, StringComparer.Ordinal))
{
    var users = originalPackages.Where(p => propertyByKey.GetValueOrDefault(p.Key) == property).ToList();
    var version = string.Join(", ", versions);
    var reason =
        users.Any(p => !p.IsExternalDependency) ? "the property is used outside the external dependencies ItemGroup" :
        users.FirstOrDefault(p => IsDarcDependency(versionDetailsXml, p.Id)) is { } darc ? $"{darc.Id} is managed by Darc in eng/Version.Details.xml" :
        !Property.IsDefined(versionsProps, property) ? "the property is not defined in eng/Versions.props" :
        versions.Count > 1 ? "packages sharing the property resolved to different versions" :
        null;

    if (reason is null)
    {
        versionsProps = Property.SetValue(versionsProps, property, versions.Single());
        updated.Add($"Updated $({property}) to {version} in eng/Versions.props.");
    }
    else
    {
        skipped.Add($"::warning::Kept $({property}) unchanged instead of updating it to {version}: {reason}.");
    }
}

File.WriteAllText(PackagesPropsPath, packagesProps);
File.WriteAllText(versionsPropsPath, versionsProps);

foreach (var message in updated.Concat(skipped))
{
    Console.WriteLine(message);
}

return 0;

static bool IsDarcDependency(string versionDetailsXml, string packageId) =>
    Regex.IsMatch(versionDetailsXml, $@"<Dependency\s+Name=""{Regex.Escape(packageId)}""", RegexOptions.IgnoreCase);

// A <PackageVersion Include="..." Version="..." /> or <PackageVersion Update="..." Version="..." />
// element and its offset in the file. Key pairs the element with the same element in another copy
// of the file: the package ID plus its occurrence number, because a package can appear in several
// ItemGroups (for example, a .NET 10 pin and a .NET 11 override).
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
            var id = IdAttribute().Match(match.Value);
            var version = VersionAttribute().Match(match.Value);
            if (!id.Success || !version.Success)
            {
                continue;
            }

            var packageId = id.Groups["id"].Value;
            var occurrence = occurrences.GetValueOrDefault(packageId);
            occurrences[packageId] = occurrence + 1;
            packages.Add(new PackageVersion(
                packageId,
                version.Groups[1].Value,
                $"{packageId.ToLowerInvariant()}#{occurrence}",
                match.Value,
                match.Index,
                match.Index > groupStart && match.Index < groupEnd));
        }

        return packages;
    }

    public static string? ReadPropertyReference(string version) =>
        MSBuildPropertyReference().Match(version) is { Success: true } match ? match.Groups[1].Value : null;

    public static string ReplaceVersion(string element, string version) =>
        VersionAttribute().Replace(element, $"Version=\"{version}\"", count: 1);

    [GeneratedRegex(@"<PackageVersion\b[^>]*>")]
    private static partial Regex PackageVersionElement();

    // \b keeps these from matching the end of a longer attribute name, such as MinVersion.
    [GeneratedRegex(@"\b(?:Include|Update)=""(?<id>[^""]*)""")]
    private static partial Regex IdAttribute();

    [GeneratedRegex(@"\bVersion=""([^""]*)""")]
    private static partial Regex VersionAttribute();

    // Matches a Version attribute value that is exactly one MSBuild property reference and captures
    // the property name: "$(OpenTelemetryInstrumentationHttpVersion)" -> OpenTelemetryInstrumentationHttpVersion.
    // Literal versions ("1.21.0") and expressions ("$(A)-$(B)") don't match, so they are left alone.
    [GeneratedRegex(@"^\$\(([A-Za-z_][A-Za-z0-9_.-]*)\)$")]
    private static partial Regex MSBuildPropertyReference();
}

// A simple property definition on its own line, such as
//   <OpenTelemetryInstrumentationHttpVersion>1.21.0</OpenTelemetryInstrumentationHttpVersion>
// LineStart and LineLength cover the whole line, including indentation and the line ending,
// so the definition can be removed without leaving a blank line.
internal sealed partial record Property(string Name, string Value, int LineStart, int LineLength)
{
    public static List<Property> ReadAll(string content) =>
        PropertyLine().Matches(content)
            .Select(m => new Property(m.Groups["name"].Value, m.Groups["value"].Value, m.Index, m.Length))
            .ToList();

    public static bool IsDefined(string content, string name) =>
        Regex.IsMatch(content, Definition(name));

    public static string SetValue(string content, string name, string value) =>
        Regex.Replace(content, Definition(name), $"<{name}>{value}</{name}>");

    private static string Definition(string name) =>
        $"<{Regex.Escape(name)}>[^<]*</{Regex.Escape(name)}>";

    [GeneratedRegex(@"^[ \t]*<(?<name>[A-Za-z_][A-Za-z0-9_.-]*)>(?<value>[^<]*)</\k<name>>[ \t]*\r?\n", RegexOptions.Multiline)]
    private static partial Regex PropertyLine();
}
