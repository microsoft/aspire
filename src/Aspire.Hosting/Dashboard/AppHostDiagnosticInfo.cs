// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text;
using Aspire.Shared;

namespace Aspire.Hosting.Dashboard;

/// <summary>
/// Builds the human-readable AppHost description that the AppHost forwards to the dashboard
/// (via <c>DASHBOARD__APPHOST__INFO</c>) for inclusion in feedback issues. The AppHost computes
/// this itself because it is the running app and therefore knows its exact Aspire SDK/package
/// versions and target framework, so the dashboard never has to re-run MSBuild (or Node.js) to
/// discover them.
/// </summary>
internal static class AppHostDiagnosticInfo
{
    private const string AppHostSdkName = "Aspire.AppHost.Sdk";
    private const string AppHostPackageName = "Aspire.Hosting.AppHost";

    // Emitted as AssemblyMetadata by the Aspire.AppHost.Sdk targets (see Aspire.Hosting.AppHost.in.targets).
    // Absent when the AppHost was built with an older SDK, in which case the SDK clause is simply omitted.
    private const string AppHostSdkVersionMetadataKey = "Aspire.AppHost.Sdk.Version";

    /// <summary>
    /// Describes the running AppHost from its file path and loaded assemblies, or <see langword="null"/>
    /// when no AppHost is configured or the file shape is not recognized.
    /// </summary>
    public static string? Describe(string? appHostFilePath, Assembly? appHostAssembly, Assembly hostingAssembly)
    {
        if (string.IsNullOrEmpty(appHostFilePath))
        {
            return null;
        }

        // Both C# AppHosts (a .csproj project AppHost or a single-file apphost.cs) and polyglot
        // TypeScript AppHosts run on the .NET host that executes this code: a TypeScript AppHost is
        // driven by the .NET RemoteHost backend, which still runs this hosting pipeline. Other,
        // unrecognized shapes return null rather than being mislabeled.
        var extension = Path.GetExtension(appHostFilePath).ToLowerInvariant();
        var (language, isDotNetAppHost) = extension switch
        {
            ".csproj" or ".cs" => ((string?)"C#", true),
            ".ts" or ".mts" => ((string?)"TypeScript", false),
            _ => ((string?)null, false)
        };

        if (language is null)
        {
            return null;
        }

        var fileName = Path.GetFileName(appHostFilePath);

        // The running Aspire.Hosting assembly version is the authoritative Aspire.Hosting.AppHost
        // package version: they ship in lockstep, and this is exactly the build that is executing.
        // It is meaningful for both C# and TypeScript AppHosts because both run against this build.
        var packageVersion = AssemblyVersionHelper.GetDisplayVersion(hostingAssembly);

        // The MSBuild SDK version and target framework only apply to a .NET AppHost. For a TypeScript
        // AppHost there is no Aspire.AppHost.Sdk, and the loaded assembly's target framework belongs to
        // the .NET RemoteHost backend rather than the AppHost, so both clauses are omitted.
        string? sdkVersion = null;
        string? targetFramework = null;
        if (isDotNetAppHost)
        {
            sdkVersion = GetAssemblyMetadataValue(appHostAssembly, AppHostSdkVersionMetadataKey);
            targetFramework = GetTargetFrameworkMoniker(appHostAssembly);
        }

        return Format(language, fileName, sdkVersion, packageVersion, targetFramework);
    }

    /// <summary>
    /// Formats the AppHost description, omitting any clause whose value is absent. For example:
    /// <c>C# (`MyApp.AppHost.csproj`) using Aspire.AppHost.Sdk 13.5.0 and Aspire.Hosting.AppHost 9.0.0 targeting `net10.0`</c>.
    /// </summary>
    public static string Format(string language, string fileName, string? sdkVersion, string? packageVersion, string? targetFramework)
    {
        var builder = new StringBuilder();

        // Use only the file name, never the absolute path: the path would leak the local user/home
        // directory (and customer folder names) into a public GitHub issue. The file name (for
        // example MyApp.AppHost.csproj or apphost.cs) is enough to identify the AppHost shape.
        builder.Append(CultureInfo.InvariantCulture, $"{language} (`{fileName}`)");

        // The first version clause is introduced with "using" and any subsequent clause with "and",
        // so the sentence still reads correctly when the SDK version is missing but the package
        // version is present (and vice versa).
        var startedUsingClause = false;
        if (!string.IsNullOrWhiteSpace(sdkVersion))
        {
            builder.Append(CultureInfo.InvariantCulture, $" using {AppHostSdkName} {sdkVersion}");
            startedUsingClause = true;
        }

        if (!string.IsNullOrWhiteSpace(packageVersion))
        {
            var connector = startedUsingClause ? "and" : "using";
            builder.Append(CultureInfo.InvariantCulture, $" {connector} {AppHostPackageName} {packageVersion}");
        }

        if (!string.IsNullOrWhiteSpace(targetFramework))
        {
            builder.Append(CultureInfo.InvariantCulture, $" targeting `{targetFramework}`");
        }

        return builder.ToString();
    }

    /// <summary>
    /// Reads the target framework moniker (for example <c>net10.0</c>) from an assembly's
    /// <see cref="TargetFrameworkAttribute"/>, or <see langword="null"/> when it cannot be determined.
    /// </summary>
    public static string? GetTargetFrameworkMoniker(Assembly? assembly)
    {
        // TargetFrameworkAttribute.FrameworkName is the long form, for example ".NETCoreApp,Version=v10.0".
        var frameworkName = assembly?.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName;
        return ConvertToTargetFrameworkMoniker(frameworkName);
    }

    /// <summary>
    /// Converts a <see cref="TargetFrameworkAttribute.FrameworkName"/> value such as
    /// <c>.NETCoreApp,Version=v10.0</c> into the short moniker <c>net10.0</c>, or <see langword="null"/>
    /// for frameworks that don't use the short form.
    /// </summary>
    public static string? ConvertToTargetFrameworkMoniker(string? frameworkName)
    {
        if (string.IsNullOrEmpty(frameworkName))
        {
            return null;
        }

        try
        {
            // Only .NET 5+ (.NETCoreApp v5.0 and later) uses the short "netX.Y" moniker; older
            // identifiers/profiles (e.g. ".NETFramework,Version=v4.8") are omitted rather than shown
            // in a form that wouldn't round-trip to a usable TFM.
            var parsed = new FrameworkName(frameworkName);
            if (string.Equals(parsed.Identifier, ".NETCoreApp", StringComparison.OrdinalIgnoreCase) && parsed.Version.Major >= 5)
            {
                return $"net{parsed.Version.Major}.{parsed.Version.Minor}";
            }
        }
        catch (ArgumentException)
        {
            // FrameworkName throws ArgumentException for an unrecognized shape; omit the TFM in that case.
        }

        return null;
    }

    private static string? GetAssemblyMetadataValue(Assembly? assembly, string key)
    {
        if (assembly is null)
        {
            return null;
        }

        foreach (var attribute in assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            if (string.Equals(attribute.Key, key, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(attribute.Value))
            {
                return attribute.Value;
            }
        }

        return null;
    }
}
