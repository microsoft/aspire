// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Projects;

/// <summary>
/// Provides fallback parsing capabilities when MSBuild evaluation fails.
/// Supports both .csproj XML files and .cs single-file apphost files.
/// Used primarily for AppHost projects with unresolvable SDK versions.
/// </summary>
internal sealed partial class FallbackProjectParser
{
    private readonly ILogger<FallbackProjectParser> _logger;

    [GeneratedRegex(@"#:sdk\s+Aspire\.AppHost\.Sdk@([\d\.\-a-zA-Z]+|\*)")]
    private static partial Regex SdkDirectiveRegex();

    [GeneratedRegex(@"#:package\s+([a-zA-Z0-9\._]+)@([\d\.\-a-zA-Z]+|\*)")]
    private static partial Regex PackageDirectiveRegex();

    public FallbackProjectParser(ILogger<FallbackProjectParser> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Parses a project file using direct parsing to extract basic project information.
    /// Returns a synthetic JsonDocument that mimics MSBuild's GetProjectItemsAndProperties output.
    /// Supports both .csproj XML files and .cs single-file apphost files.
    /// </summary>
    public JsonDocument ParseProject(FileInfo projectFile)
    {
        try
        {
            _logger.LogDebug("Parsing project file '{ProjectFile}' using fallback parser", projectFile.FullName);

            // Detect file type and route to appropriate parser
            if (string.Equals(projectFile.Extension, ".csproj", StringComparison.OrdinalIgnoreCase))
            {
                return ParseCsprojProjectFile(projectFile);
            }
            else if (string.Equals(projectFile.Extension, ".cs", StringComparison.OrdinalIgnoreCase))
            {
                return ParseCsAppHostFile(projectFile);
            }
            else
            {
                throw new ProjectUpdaterException($"Unsupported project file type: {projectFile.Extension}. Expected .csproj or .cs file.");
            }
        }
        catch (ProjectUpdaterException)
        {
            // Re-throw our custom exceptions
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse project file '{ProjectFile}' using fallback parser", projectFile.FullName);
            throw new ProjectUpdaterException($"Failed to parse project file '{projectFile.FullName}' using fallback parser: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Parses a .csproj XML project file to extract SDK and package information.
    /// </summary>
    private static JsonDocument ParseCsprojProjectFile(FileInfo projectFile)
    {
        var doc = XDocument.Load(projectFile.FullName);
        var root = doc.Root;

        if (root?.Name.LocalName != "Project")
        {
            throw new InvalidOperationException($"Invalid project file format: {projectFile.FullName}");
        }

        // Extract SDK information
        var aspireHostingSdkVersion = ExtractAspireHostingSdkVersion(root);

        // Extract package references
        var packageReferences = ExtractPackageReferences(root);

        // Extract project references
        var projectReferences = ExtractProjectReferences(root, projectFile);

        return BuildJsonDocument(aspireHostingSdkVersion, packageReferences, projectReferences);
    }

    /// <summary>
    /// Parses a .cs single-file apphost to extract SDK and package information from directives.
    /// </summary>
    private static JsonDocument ParseCsAppHostFile(FileInfo projectFile)
    {
        var fileContent = File.ReadAllText(projectFile.FullName);

        // Extract SDK version from #:sdk directive
        var aspireHostingSdkVersion = ExtractSdkVersionFromDirective(fileContent);

        // Extract package references from #:package directives
        var packageReferences = ExtractPackageReferencesFromDirectives(fileContent);

        // Single-file apphost projects don't have project references
        var projectReferences = Array.Empty<ProjectReferenceInfo>();

        return BuildJsonDocument(aspireHostingSdkVersion, packageReferences, projectReferences);
    }

    /// <summary>
    /// Builds a synthetic JsonDocument from extracted project information.
    /// </summary>
    private static JsonDocument BuildJsonDocument(
        string? aspireHostingSdkVersion,
        PackageReferenceInfo[] packageReferences,
        ProjectReferenceInfo[] projectReferences)
    {
        var rootObject = new JsonObject();

        // Items section
        var itemsObject = new JsonObject();

        // PackageReference items
        var packageRefArray = new JsonArray();
        foreach (var pkg in packageReferences)
        {
            var packageObj = new JsonObject();
            packageObj["Identity"] = JsonValue.Create(pkg.Identity);
            packageObj["Version"] = JsonValue.Create(pkg.Version);
            packageRefArray.Add((JsonNode?)packageObj);
        }
        itemsObject["PackageReference"] = packageRefArray;

        // ProjectReference items
        var projectRefArray = new JsonArray();
        foreach (var proj in projectReferences)
        {
            var projectObj = new JsonObject();
            projectObj["Identity"] = JsonValue.Create(proj.Identity);
            projectObj["FullPath"] = JsonValue.Create(proj.FullPath);
            projectRefArray.Add((JsonNode?)projectObj);
        }
        itemsObject["ProjectReference"] = projectRefArray;

        rootObject["Items"] = itemsObject;

        // Properties section
        var propertiesObject = new JsonObject();
        propertiesObject["AspireHostingSDKVersion"] = JsonValue.Create(aspireHostingSdkVersion);
        rootObject["Properties"] = propertiesObject;

        // Fallback flag
        rootObject["Fallback"] = JsonValue.Create(true);

        // Convert JsonObject to JsonDocument
        return JsonDocument.Parse(rootObject.ToJsonString());
    }

    private static string? ExtractAspireHostingSdkVersion(XElement projectRoot)
    {
        // Check new format first: <Project Sdk="Aspire.AppHost.Sdk/version">
        // The Sdk attribute may contain multiple SDKs separated by semicolons
        var sdkAttribute = projectRoot.Attribute("Sdk")?.Value;
        if (sdkAttribute is not null)
        {
            var version = ExtractVersionFromSdkAttribute(sdkAttribute);
            if (version is not null)
            {
                return version;
            }
        }

        // Fall back to old format: <Sdk Name="Aspire.AppHost.Sdk" Version="..." />
        var sdkElement = projectRoot
            .Elements("Sdk")
            .FirstOrDefault(e => e.Attribute("Name")?.Value == "Aspire.AppHost.Sdk");

        return sdkElement?.Attribute("Version")?.Value;
    }

    /// <summary>
    /// Extracts the Aspire.AppHost.Sdk version from an Sdk attribute value.
    /// Handles formats like "Aspire.AppHost.Sdk/13.0.1" or "Aspire.AppHost.Sdk/13.0.1;Microsoft.NET.Sdk".
    /// </summary>
    private static string? ExtractVersionFromSdkAttribute(string sdkAttribute)
    {
        const string sdkPrefix = "Aspire.AppHost.Sdk";

        // Split by semicolon in case of multiple SDKs
        var sdks = sdkAttribute.Split(';');
        foreach (var sdk in sdks)
        {
            var trimmedSdk = sdk.Trim();

            // Check for exact match "Aspire.AppHost.Sdk" (no version)
            if (trimmedSdk.Equals(sdkPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return null; // SDK found but no version specified
            }

            // Check for "Aspire.AppHost.Sdk/version" format
            if (trimmedSdk.StartsWith(sdkPrefix + "/", StringComparison.OrdinalIgnoreCase))
            {
                var version = trimmedSdk.Substring(sdkPrefix.Length + 1);
                return string.IsNullOrEmpty(version) ? null : version;
            }
        }

        return null;
    }

    private static PackageReferenceInfo[] ExtractPackageReferences(XElement projectRoot)
    {
        var packageReferences = new List<PackageReferenceInfo>();

        // Find all PackageReference elements
        var packageRefElements = projectRoot
            .Descendants("PackageReference")
            .Where(e => !string.IsNullOrEmpty(e.Attribute("Include")?.Value) || !string.IsNullOrEmpty(e.Attribute("Update")?.Value));

        foreach (var element in packageRefElements)
        {
            var identity = element.Attribute("Include")?.Value ?? element.Attribute("Update")?.Value;
            if (string.IsNullOrEmpty(identity))
            {
                continue;
            }

            // Try to get version from attribute first, then from child element
            var version = element.Attribute("Version")?.Value ??
                         element.Element("Version")?.Value;

            var packageRef = new PackageReferenceInfo
            {
                Identity = identity,
                Version = version ?? string.Empty
            };

            packageReferences.Add(packageRef);
        }

        return packageReferences.ToArray();
    }

    private static ProjectReferenceInfo[] ExtractProjectReferences(XElement projectRoot, FileInfo projectFile)
    {
        var projectReferences = new List<ProjectReferenceInfo>();

        // Find all ProjectReference elements
        var projectRefElements = projectRoot
            .Descendants("ProjectReference")
            .Where(e => !string.IsNullOrEmpty(e.Attribute("Include")?.Value));

        foreach (var element in projectRefElements)
        {
            var include = element.Attribute("Include")?.Value;
            if (string.IsNullOrEmpty(include))
            {
                continue;
            }

            // Convert relative path to absolute path
            var fullPath = Path.IsPathRooted(include)
                ? include
                : Path.GetFullPath(Path.Combine(projectFile.DirectoryName!, include));

            var projectRef = new ProjectReferenceInfo
            {
                Identity = include,
                FullPath = fullPath
            };

            projectReferences.Add(projectRef);
        }

        return projectReferences.ToArray();
    }

    /// <summary>
    /// Extracts the Aspire.AppHost.Sdk version from the #:sdk directive in a single-file apphost.
    /// </summary>
    private static string? ExtractSdkVersionFromDirective(string fileContent)
    {
        // Match: #:sdk Aspire.AppHost.Sdk@<version>
        // Where version can be a semantic version or wildcard (*)
        var match = SdkDirectiveRegex().Match(fileContent);

        if (match.Success)
        {
            return match.Groups[1].Value;
        }

        return null;
    }

    /// <summary>
    /// Extracts package references from #:package directives in a single-file apphost.
    /// </summary>
    private static PackageReferenceInfo[] ExtractPackageReferencesFromDirectives(string fileContent)
    {
        var packageReferences = new List<PackageReferenceInfo>();

        // Match: #:package <PackageId>@<version>
        // Where version can be a semantic version or wildcard (*)
        var matches = PackageDirectiveRegex().Matches(fileContent);

        foreach (Match match in matches)
        {
            var identity = match.Groups[1].Value;
            var version = match.Groups[2].Value;

            var packageRef = new PackageReferenceInfo
            {
                Identity = identity,
                Version = version
            };

            packageReferences.Add(packageRef);
        }

        return packageReferences.ToArray();
    }
}

internal record PackageReferenceInfo
{
    public required string Identity { get; init; }
    public required string Version { get; init; }
}

internal record ProjectReferenceInfo
{
    public required string Identity { get; init; }
    public required string FullPath { get; init; }
}
