// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace Aspire.Cli.Templating;

/// <summary>
/// Resolves the third-party package versions stamped into the Aspire.Cli assembly via
/// <see cref="AssemblyMetadataAttribute"/> (see Aspire.Cli.csproj). Embedded templates
/// that reference these packages (today: <c>csharp-servicedefaults</c>) use this helper
/// to substitute version tokens like <c>{{microsoftExtensionsHttpResilienceVersion}}</c>
/// at render time instead of relying on the
/// <c>!!REPLACE_WITH_*_VERSION!!</c> patcher that runs when
/// <c>Aspire.ProjectTemplates</c> is packed.
/// </summary>
/// <remarks>
/// Keep the keys here in sync with the <c>&lt;AssemblyMetadata&gt;</c> items in
/// <c>src/Aspire.Cli/Aspire.Cli.csproj</c> and with the <c>&lt;Replacements&gt;</c>
/// list in <c>src/Aspire.ProjectTemplates/Aspire.ProjectTemplates.csproj</c>.
/// </remarks>
internal static class EmbeddedTemplatePackageVersions
{
    private static readonly Lazy<IReadOnlyDictionary<string, string>> s_metadata = new(LoadMetadata);

    public static string MicrosoftExtensionsHttpResilienceVersion
        => Get("MicrosoftExtensionsHttpResilienceVersion");

    public static string MicrosoftExtensionsServiceDiscoveryVersion
        => Get("MicrosoftExtensionsServiceDiscoveryVersion");

    public static string OpenTelemetryExporterOpenTelemetryProtocolVersion
        => Get("OpenTelemetryExporterOpenTelemetryProtocolVersion");

    public static string OpenTelemetryInstrumentationExtensionsHostingVersion
        => Get("OpenTelemetryInstrumentationExtensionsHostingVersion");

    public static string OpenTelemetryInstrumentationAspNetCoreVersion
        => Get("OpenTelemetryInstrumentationAspNetCoreVersion");

    public static string OpenTelemetryInstrumentationHttpVersion
        => Get("OpenTelemetryInstrumentationHttpVersion");

    public static string OpenTelemetryInstrumentationRuntimeVersion
        => Get("OpenTelemetryInstrumentationRuntimeVersion");

    public static string MicrosoftAspNetCoreOpenApiPreviewVersion
        => Get("MicrosoftAspNetCoreOpenApiPreviewVersion");

    private static string Get(string key)
    {
        if (s_metadata.Value.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        throw new InvalidOperationException(
            $"Embedded template package version '{key}' was not baked into the Aspire.Cli assembly. " +
            $"Add an <AssemblyMetadata Include=\"{key}\" Value=\"$({key})\" /> entry in src/Aspire.Cli/Aspire.Cli.csproj.");
    }

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:RequiresUnreferencedCode",
        Justification = "AssemblyMetadataAttribute is preserved by default; we only read the well-known string Key/Value properties.")]
    private static IReadOnlyDictionary<string, string> LoadMetadata()
    {
        var assembly = typeof(EmbeddedTemplatePackageVersions).Assembly;
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var attribute in assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            if (attribute.Key is { } key && attribute.Value is { } value)
            {
                metadata[key] = value;
            }
        }

        return metadata;
    }
}
