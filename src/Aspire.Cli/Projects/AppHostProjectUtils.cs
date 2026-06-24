// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Xml;
using System.Xml.Linq;

namespace Aspire.Cli.Projects;

internal static class AppHostProjectUtils
{
    private const string AspireAppHostSdkName = "Aspire.AppHost.Sdk";

    /// <summary>
    /// Determines whether a project or single-file source likely represents an Aspire AppHost.
    /// </summary>
    /// <remarks>
    /// This is used on discovery/validation paths that intentionally skip a full MSBuild evaluation.
    /// It prefers authoritative content signals parsed directly from
    /// the project file over the file/folder name, because the name alone produces both false
    /// positives (a non-Aspire project named <c>Foo.AppHost.csproj</c>) and false negatives (a real
    /// AppHost project with a non-conventional name). The name-based heuristic is kept only as a
    /// fallback for when the content cannot be read or parsed.
    /// </remarks>
    internal static bool IsLikelyAppHost(FileInfo projectFile)
    {
        // Prefer authoritative content signals (parsed csproj XML, or the single-file SDK directive)
        // and only fall back to the name heuristic when the content is missing or unparseable.
        var contentSignal = projectFile.Extension switch
        {
            var ext when ext.Equals(".csproj", StringComparison.OrdinalIgnoreCase) => TryDetectAppHostFromCsproj(projectFile),
            var ext when ext.Equals(".cs", StringComparison.OrdinalIgnoreCase) => TryDetectAppHostFromSingleFile(projectFile),
            _ => null,
        };

        if (contentSignal.HasValue)
        {
            return contentSignal.Value;
        }

        return MatchesAppHostNameHeuristics(projectFile);
    }

    /// <summary>
    /// Inspects a <c>.csproj</c> for Aspire AppHost signals. Returns <see langword="true"/>/<see langword="false"/>
    /// when the project XML can be parsed, or <see langword="null"/> when it cannot (so the caller falls back
    /// to the name heuristic).
    /// </summary>
    private static bool? TryDetectAppHostFromCsproj(FileInfo projectFile)
    {
        XDocument document;
        try
        {
            document = XDocument.Load(projectFile.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            // The csproj is missing, unreadable, or not well-formed XML. Signal "unknown" so the
            // caller can fall back to the name heuristic instead of treating it as a definitive "no".
            return null;
        }

        var root = document.Root;
        if (root is null)
        {
            return null;
        }

        // 1) SDK-style reference declared via the Sdk attribute on <Project>, which may list multiple
        //    SDKs with optional versions, e.g.:
        //      <Project Sdk="Microsoft.NET.Sdk;Aspire.AppHost.Sdk/9.0.0">
        var sdkAttribute = root.Attribute("Sdk")?.Value;
        if (sdkAttribute is not null && ContainsAspireAppHostSdk(sdkAttribute))
        {
            return true;
        }

        // The remaining checks compare on Name.LocalName so that projects declaring the legacy MSBuild
        // XML namespace (xmlns="http://schemas.microsoft.com/developer/msbuild/2003") are matched the
        // same as SDK-style projects that omit it.

        // 2) Nested SDK reference element, e.g.:
        //      <Sdk Name="Aspire.AppHost.Sdk" Version="9.0.0" />
        var hasSdkElement = root.Descendants()
            .Any(e => e.Name.LocalName.Equals("Sdk", StringComparison.Ordinal)
                && string.Equals(e.Attribute("Name")?.Value, AspireAppHostSdkName, StringComparison.OrdinalIgnoreCase));
        if (hasSdkElement)
        {
            return true;
        }

        // 3) Explicit <IsAspireHost>true</IsAspireHost> property. The Aspire.AppHost.Sdk sets this
        //    during evaluation, but it can also appear literally in the project file.
        var hasIsAspireHost = root.Descendants()
            .Any(e => e.Name.LocalName.Equals("IsAspireHost", StringComparison.Ordinal)
                && string.Equals(e.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase));
        if (hasIsAspireHost)
        {
            return true;
        }

        // Parsed successfully and found no Aspire AppHost signals — authoritatively not an AppHost.
        return false;
    }

    /// <summary>
    /// Inspects a single-file (file-based) AppHost source for the SDK directive. Returns
    /// <see langword="true"/>/<see langword="false"/> when the file can be read, or <see langword="null"/>
    /// when it cannot (so the caller falls back to the name heuristic).
    /// </summary>
    private static bool? TryDetectAppHostFromSingleFile(FileInfo candidateFile)
    {
        // A single-file AppHost declares the SDK with a directive near the top of the file, e.g.:
        //   #:sdk Aspire.AppHost.Sdk
        //   #:sdk Aspire.AppHost.Sdk@9.0.0
        try
        {
            using var reader = candidateFile.OpenText();
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (line.TrimStart().StartsWith($"#:sdk {AspireAppHostSdkName}", StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        // Read the whole file and the directive was absent — not a single-file AppHost.
        return false;
    }

    /// <summary>
    /// Name-based fallback used when the project content cannot be read or parsed.
    /// </summary>
    private static bool MatchesAppHostNameHeuristics(FileInfo projectFile)
    {
        var fileNameSuggestsAppHost = projectFile.Name.EndsWith("AppHost.csproj", StringComparison.OrdinalIgnoreCase);
        var folderContainsAppHostCSharpFile = projectFile.Directory?
            .EnumerateFiles("*", SearchOption.TopDirectoryOnly)
            .Any(f => f.Name.Equals("AppHost.cs", StringComparison.OrdinalIgnoreCase)) ?? false;

        return fileNameSuggestsAppHost || folderContainsAppHostCSharpFile;
    }

    /// <summary>
    /// Checks whether an <c>Sdk</c> attribute value references the Aspire.AppHost.Sdk. The value may
    /// list multiple SDKs separated by semicolons, each optionally suffixed with a version, e.g.
    /// "Aspire.AppHost.Sdk/13.0.1" or "Aspire.AppHost.Sdk/13.0.1;Microsoft.NET.Sdk".
    /// </summary>
    private static bool ContainsAspireAppHostSdk(string sdkAttribute)
    {
        var sdks = sdkAttribute.Split(';');
        foreach (var sdk in sdks)
        {
            var trimmedSdk = sdk.Trim();

            if (trimmedSdk.Equals(AspireAppHostSdkName, StringComparison.OrdinalIgnoreCase) ||
                trimmedSdk.StartsWith(AspireAppHostSdkName + "/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
