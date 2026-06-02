// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Cli.Utils;
using Aspire.Shared;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Templating;

/// <summary>
/// Renders the embedded "ASP.NET Core / React, C# AppHost" starter template
/// (AppHost + Server + a Vite/React <c>frontend</c> — no .sln, no
/// <c>.template.config</c>) into an output directory. This is the embedded-engine
/// replacement for the previous flow that resolved the
/// <c>Aspire.ProjectTemplates</c> NuGet package, installed it via
/// <c>dotnet new install</c>, and then invoked <c>dotnet new aspire-ts-cs-starter</c>.
/// </summary>
/// <remarks>
/// Symbol translation from the upstream <c>aspire-ts-cs-starter</c>
/// <c>template.json</c> (the source of truth for layout; this template was
/// pre-processed at embed time to fold static conditionals):
/// <list type="bullet">
/// <item><c>sourceName: Aspire-StarterApplication.1</c> → <c>{{projectName}}</c>
/// token, applied to both file contents and the <c>{{projectName}}.*._csproj</c>
/// filenames via the path transformer.</item>
/// <item><c>GeneratedClassNamePrefix</c> regex symbol → <c>{{generatedClassNamePrefix}}</c>;
/// computed in C# via <see cref="EmbeddedCSharpStarterTemplate.ComputeGeneratedClassNamePrefix"/>.</item>
/// <item><c>XmlEncodedProjectName</c> derived symbol → the raw project name. The
/// CLI rejects project names containing characters that need XML-encoding before
/// reaching the renderer, so the rendered <c>&lt;ProjectReference&gt;</c> include
/// (authored with the <c>sourceName</c> literal) is safe with the raw name.</item>
/// <item><c>Framework</c> choice → hardcoded <c>net10.0</c>. The
/// <c>Framework != 'net8.0'</c> OpenAPI branches are kept and the <c>net9.0</c>
/// <c>Microsoft.AspNetCore.OpenApi</c> reference was dropped at embed time.</item>
/// <item><c>HasHttpsProfile</c> computed symbol → hardcoded <c>true</c>.</item>
/// <item><c>UseRedisCache</c> bool → <c>{{#useRedisCache}}</c> /
/// <c>{{^useRedisCache}}</c> blocks in AppHost.cs, Server Program.cs, the AppHost
/// csproj, and the Server csproj.</item>
/// <item><c>LocalhostTld</c> bool → <c>{{#localhostTld}}</c> /
/// <c>{{^localhostTld}}</c> blocks in the AppHost launchSettings.json; the
/// <c>hostName</c> derived symbol is computed via
/// <see cref="EmbeddedCSharpAppHostTemplate.ComputeLocalhostTldHostName"/> and
/// surfaced as <c>{{hostName}}</c>.</item>
/// <item><c>UserSecretsId</c> → freshly generated GUID per template application.</item>
/// <item>The AppHost <c>port</c> + <c>coalesce</c> generators → resolved by
/// <see cref="AppHostProfilePortGenerator"/>; the two Server ports →
/// <see cref="ServerProfilePortGenerator"/>.</item>
/// <item><c>.sln</c> / <c>.template.config</c> → dropped; they have no consumer
/// outside <c>dotnet new</c>.</item>
/// <item>Package versions (<c>!!REPLACE_WITH_*!!</c>) →
/// <see cref="EmbeddedTemplatePackageVersions"/> for the OTel / Extensions /
/// OpenAPI versions baked into Aspire.Cli at build time, plus
/// <see cref="VersionHelper.GetDefaultTemplateVersion"/> for Aspire's own version
/// (SDK reference, Aspire.Hosting.JavaScript, Aspire.Hosting.Redis,
/// Aspire.StackExchange.Redis.OutputCaching).</item>
/// </list>
/// </remarks>
internal static class EmbeddedTsCsStarterTemplate
{
    /// <summary>
    /// Writes the embedded ASP.NET Core / React starter template into
    /// <paramref name="outputPath"/>. The output directory is created if it does
    /// not already exist; existing files are overwritten.
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
        var serverPorts = ServerProfilePortGenerator.Generate(Random.Shared);
        var userSecretsId = Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture);
        var hostName = EmbeddedCSharpAppHostTemplate.ComputeLocalhostTldHostName(projectName);
        var generatedClassNamePrefix = EmbeddedCSharpStarterTemplate.ComputeGeneratedClassNamePrefix(projectName);

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
            ["serverHttpPort"] = serverPorts.ServerHttpPort.ToString(CultureInfo.InvariantCulture),
            ["serverHttpsPort"] = serverPorts.ServerHttpsPort.ToString(CultureInfo.InvariantCulture),
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
            "Rendering embedded ts-cs starter template to '{OutputPath}' (project '{ProjectName}', Aspire version '{AspireVersion}', UseRedisCache={UseRedisCache}, LocalhostTld={LocalhostTld}).",
            outputPath,
            projectName,
            aspireVersion,
            useRedisCache,
            useLocalhostTld);

        if (!Directory.Exists(outputPath))
        {
            Directory.CreateDirectory(outputPath);
        }

        var source = new EmbeddedResourceTemplateSource(typeof(EmbeddedTsCsStarterTemplate).Assembly, "ts-cs-starter");
        var renderer = new ManifestTemplateRenderer(logger);
        await renderer.RenderAsync(source, outputPath, symbols, conditions, cancellationToken);
    }
}
