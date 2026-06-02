// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.RegularExpressions;
using Aspire.Cli.Utils;
using Aspire.Shared;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Templating;

/// <summary>
/// Renders the embedded C# multi-project "starter" template
/// (AppHost + ApiService + Web + ServiceDefaults — no Tests project, no .sln,
/// no <c>.template.config</c>) into an output directory. This is the
/// embedded-engine replacement for the previous flow that resolved the
/// <c>Aspire.ProjectTemplates</c> NuGet package, installed it via
/// <c>dotnet new install</c>, and then invoked <c>dotnet new aspire-starter</c>.
/// </summary>
/// <remarks>
/// Symbol translation from the upstream <c>aspire-starter</c>
/// <c>template.json</c> (the source of truth for layout; this template was
/// pre-processed at embed time to fold static conditionals — see the docs at
/// the top of the embedded source tree):
/// <list type="bullet">
/// <item><c>sourceName: Aspire-StarterApplication.1</c> → <c>{{projectName}}</c>
/// token, applied to both file contents and the <c>{{projectName}}.*._csproj</c>
/// filenames via the path transformer.</item>
/// <item><c>GeneratedClassNamePrefix</c> regex symbol → <c>{{generatedClassNamePrefix}}</c>;
/// computed in C# via <see cref="ComputeGeneratedClassNamePrefix"/> which mirrors
/// the upstream regex <c>(((?&lt;=\.)|^)(?=\d)|\W)</c> → <c>_</c>.</item>
/// <item><c>XmlEncodedProjectName</c> derived xmlEncode symbol → <c>{{projectName}}</c>
/// directly. The CLI rejects project names containing characters that need
/// XML-encoding before reaching the renderer, so the rendered csproj
/// <c>&lt;ProjectReference&gt;</c> includes are safe with the raw name.</item>
/// <item><c>Framework</c> choice → hardcoded <c>net10.0</c>. All
/// <c>Framework == 'net8.0'</c> branches were folded out at embed time; the
/// <c>Microsoft.AspNetCore.OpenApi</c> net9 reference was dropped.</item>
/// <item><c>HasHttpsProfile</c> computed symbol → hardcoded <c>true</c>;
/// matches the AppHost-only embedded template behavior.</item>
/// <item><c>UseRedisCache</c> bool → <c>{{#useRedisCache}}</c> /
/// <c>{{^useRedisCache}}</c> Mustache blocks in AppHost.cs / Web Program.cs /
/// AppHost csproj / Web csproj.</item>
/// <item><c>LocalhostTld</c> bool → <c>{{#localhostTld}}</c> / <c>{{^localhostTld}}</c>
/// blocks in launchSettings.json files. The <c>hostName</c> derived symbol
/// (<c>lowerCaseInvariantWithHyphens</c> form chain) is computed in C# via
/// <see cref="EmbeddedCSharpAppHostTemplate.ComputeLocalhostTldHostName"/>
/// and surfaced as <c>{{hostName}}</c>.</item>
/// <item><c>UserSecretsId</c> → freshly generated GUID per template
/// application, matching the upstream built-in GUID symbol.</item>
/// <item>The 12 <c>port</c> + <c>coalesce</c> generators → resolved by
/// <see cref="AppHostProfilePortGenerator"/> (the six AppHost ports) and
/// <see cref="StarterProfilePortGenerator"/> (the four Web and ApiService
/// ports) ahead of time and injected as their respective tokens.</item>
/// <item><c>TestFx</c> / Tests project / <c>.sln</c> / <c>.template.config</c>
/// → dropped. The CLI does not surface a test framework choice today and the
/// other artifacts have no consumer outside <c>dotnet new</c>.</item>
/// <item>Package versions (<c>!!REPLACE_WITH_*!!</c>) →
/// <see cref="EmbeddedTemplatePackageVersions"/> for the OTel / Extensions
/// versions baked into Aspire.Cli at build time, plus
/// <see cref="VersionHelper.GetDefaultTemplateVersion"/> for Aspire's own
/// version (SDK reference, Aspire.Hosting.Redis, Aspire.StackExchange.Redis.OutputCaching).</item>
/// </list>
/// </remarks>
internal static class EmbeddedCSharpStarterTemplate
{
    // Mirrors the "safe namespace" form the .NET template engine derives from
    // `sourceName` for the upstream `aspire-starter` template — the form that
    // preserves '.' (so namespace segments stay intact), replaces every other
    // non-`[A-Za-z0-9_.]` character with `_`, and prefixes `_` to any digit
    // that starts a segment (start-of-string or immediately after `.`).
    //
    // The upstream template.json `GeneratedClassNamePrefix` regex symbol
    // `(((?<=\.)|^)(?=\d)|\W)` does NOT preserve `.` on its own (because `.`
    // matches `\W`); the template engine compensates by also auto-deriving the
    // safe-namespace form of `sourceName` and using it as the placeholder text
    // in the source files (e.g. `Aspire_StarterApplication._1.Web` in
    // _Imports.razor). We bake both into a single token, so the C# regex below
    // is the safe-namespace form directly.
    //
    // Examples:
    //   MyApp                        -> MyApp
    //   My-App                       -> My_App
    //   My.App.1                     -> My.App._1
    //   1MyApp                       -> _1MyApp
    //   Aspire-StarterApplication.1  -> Aspire_StarterApplication._1
    private static readonly Regex s_generatedClassNamePrefixRegex = new(
        @"((^|(?<=\.))(?=\d)|[^A-Za-z0-9_.])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Writes the embedded C# multi-project starter template into
    /// <paramref name="outputPath"/>. The output directory is created if it
    /// does not already exist; existing files are overwritten.
    /// </summary>
    public static async Task RenderAsync(
        string outputPath,
        string projectName,
        bool useRedisCache,
        bool useLocalhostTld,
        string? templateVersion,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectName);
        ArgumentNullException.ThrowIfNull(logger);

        var aspireVersion = string.IsNullOrWhiteSpace(templateVersion)
            ? VersionHelper.GetDefaultTemplateVersion()
            : templateVersion;
        var appHostPorts = AppHostProfilePortGenerator.Generate(Random.Shared);
        var starterPorts = StarterProfilePortGenerator.Generate(Random.Shared);
        var userSecretsId = Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture);
        var hostName = EmbeddedCSharpAppHostTemplate.ComputeLocalhostTldHostName(projectName);
        var generatedClassNamePrefix = ComputeGeneratedClassNamePrefix(projectName);

        // Symbol VALUES are computed here in C# (ports, GUID, derived names,
        // package versions); the template.json manifest only declares which
        // literal in the template tree maps to which of these symbols. The
        // package-version symbols come from AssemblyMetadata baked into Aspire.Cli
        // at build time (see EmbeddedTemplatePackageVersions / Aspire.Cli.csproj).
        var symbols = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["projectName"] = projectName,
            ["generatedClassNamePrefix"] = generatedClassNamePrefix,
            ["aspireVersion"] = aspireVersion,
            ["userSecretsId"] = userSecretsId,
            ["hostName"] = hostName,
            ["appHostHttpPort"] = appHostPorts.DashboardHttpPort.ToString(CultureInfo.InvariantCulture),
            ["appHostHttpsPort"] = appHostPorts.DashboardHttpsPort.ToString(CultureInfo.InvariantCulture),
            ["appHostOtlpHttpPort"] = appHostPorts.OtlpHttpPort.ToString(CultureInfo.InvariantCulture),
            ["appHostOtlpHttpsPort"] = appHostPorts.OtlpHttpsPort.ToString(CultureInfo.InvariantCulture),
            ["appHostResourceHttpPort"] = appHostPorts.ResourceServiceHttpPort.ToString(CultureInfo.InvariantCulture),
            ["appHostResourceHttpsPort"] = appHostPorts.ResourceServiceHttpsPort.ToString(CultureInfo.InvariantCulture),
            ["webHttpPort"] = starterPorts.WebHttpPort.ToString(CultureInfo.InvariantCulture),
            ["webHttpsPort"] = starterPorts.WebHttpsPort.ToString(CultureInfo.InvariantCulture),
            ["apiServiceHttpPort"] = starterPorts.ApiServiceHttpPort.ToString(CultureInfo.InvariantCulture),
            ["apiServiceHttpsPort"] = starterPorts.ApiServiceHttpsPort.ToString(CultureInfo.InvariantCulture),
            ["microsoftExtensionsHttpResilienceVersion"] = EmbeddedTemplatePackageVersions.MicrosoftExtensionsHttpResilienceVersion,
            ["microsoftExtensionsServiceDiscoveryVersion"] = EmbeddedTemplatePackageVersions.MicrosoftExtensionsServiceDiscoveryVersion,
            ["openTelemetryExporterVersion"] = EmbeddedTemplatePackageVersions.OpenTelemetryExporterOpenTelemetryProtocolVersion,
            ["openTelemetryHostingVersion"] = EmbeddedTemplatePackageVersions.OpenTelemetryInstrumentationExtensionsHostingVersion,
            ["openTelemetryInstrumentationAspNetCoreVersion"] = EmbeddedTemplatePackageVersions.OpenTelemetryInstrumentationAspNetCoreVersion,
            ["openTelemetryInstrumentationHttpVersion"] = EmbeddedTemplatePackageVersions.OpenTelemetryInstrumentationHttpVersion,
            ["openTelemetryInstrumentationRuntimeVersion"] = EmbeddedTemplatePackageVersions.OpenTelemetryInstrumentationRuntimeVersion,
            ["aspNetCoreOpenApi10Version"] = EmbeddedTemplatePackageVersions.MicrosoftAspNetCoreOpenApiPreviewVersion
        };

        var conditions = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["useRedisCache"] = useRedisCache,
            ["localhostTld"] = useLocalhostTld
        };

        logger.LogDebug(
            "Rendering embedded C# starter template to '{OutputPath}' (project '{ProjectName}', Aspire version '{AspireVersion}', UseRedisCache={UseRedisCache}, LocalhostTld={LocalhostTld}).",
            outputPath,
            projectName,
            aspireVersion,
            useRedisCache,
            useLocalhostTld);

        if (!Directory.Exists(outputPath))
        {
            Directory.CreateDirectory(outputPath);
        }

        var source = new EmbeddedResourceTemplateSource(typeof(EmbeddedCSharpStarterTemplate).Assembly, "csharp-starter");
        var renderer = new ManifestTemplateRenderer(logger);
        await renderer.RenderAsync(source, outputPath, symbols, conditions, cancellationToken);
    }

    /// <summary>
    /// Computes the C# class-name-safe prefix for namespaces and generated
    /// type names (e.g. <c>Projects.{Prefix}_AppHost</c>) using the same regex
    /// the upstream <c>aspire-starter</c> template applies. Examples:
    /// <c>Aspire-StarterApplication.1</c> → <c>Aspire_StarterApplication._1</c>;
    /// <c>1MyApp</c> → <c>_1MyApp</c>.
    /// </summary>
    internal static string ComputeGeneratedClassNamePrefix(string projectName)
    {
        return s_generatedClassNamePrefixRegex.Replace(projectName, "_");
    }
}
