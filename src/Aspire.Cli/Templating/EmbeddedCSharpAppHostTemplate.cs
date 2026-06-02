// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text;
using Aspire.Cli.Utils;
using Aspire.Shared;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Templating;

/// <summary>
/// Renders the embedded C# multi-file AppHost project template
/// (<c>{{projectName}}.csproj</c> + <c>AppHost.cs</c> +
/// <c>Properties/launchSettings.json</c> + <c>appsettings*.json</c> +
/// <c>aspire.config.json</c>) into an output directory. This is the
/// embedded-engine replacement for the previous flow that resolved the
/// <c>Aspire.ProjectTemplates</c> NuGet package, installed it via
/// <c>dotnet new install</c>, and then invoked <c>dotnet new aspire-apphost</c>.
/// </summary>
/// <remarks>
/// Symbol translation from the standalone <c>aspire-apphost</c>
/// <c>template.json</c> (kept here so future template churn doesn't drift
/// silently from the source-of-truth shipped in <c>Aspire.ProjectTemplates</c>):
/// <list type="bullet">
/// <item><c>sourceName: Aspire.AppHost1</c> → <c>{{projectName}}</c> token
/// (applied to both file contents and the <c>{{projectName}}.csproj</c>
/// filename via the path transformer).</item>
/// <item><c>Framework</c> choice → hardcoded <c>net10.0</c>. The CLI is
/// authoritative for the targeted framework; the standalone NuGet template
/// still offers the choice for <c>dotnet new</c> consumers.</item>
/// <item>The six port <c>coalesce</c> generators → resolved by
/// <see cref="AppHostProfilePortGenerator"/> ahead of time and injected as
/// <c>{{httpPort}}</c> / <c>{{httpsPort}}</c> / <c>{{otlpHttpPort}}</c> /
/// <c>{{otlpHttpsPort}}</c> / <c>{{resourceHttpPort}}</c> /
/// <c>{{resourceHttpsPort}}</c>.</item>
/// <item><c>HasHttpsProfile = !NoHttps</c> computed symbol → omitted. The
/// CLI surface for this template does not expose <c>NoHttps</c> today, so the
/// <c>https</c> profile is always emitted (matches existing CLI behavior for
/// the embedded single-file template).</item>
/// <item><c>LocalhostTld</c> bool parameter → <c>{{#localhostTld}}</c> /
/// <c>{{^localhostTld}}</c> conditional blocks in <c>launchSettings.json</c>.
/// The <c>hostName</c> derived symbol with the <c>lowerCaseInvariantWithHyphens</c>
/// form chain is computed in C# via <see cref="ComputeLocalhostTldHostName"/>
/// and surfaced as <c>{{hostName}}</c>.</item>
/// <item><c>restore</c> post-action → dropped. <c>aspire run</c> /
/// <c>aspire restore</c> already cover first-use restore for the embedded path;
/// the post-action only existed to mask <c>dotnet new</c>'s lack of implicit
/// restore.</item>
/// <item><c>UserSecretsId</c> → freshly generated GUID per template application,
/// matching what the standalone <c>dotnet new</c> template would emit via its
/// built-in GUID symbol.</item>
/// </list>
/// </remarks>
internal static class EmbeddedCSharpAppHostTemplate
{
    /// <summary>
    /// Writes the embedded C# AppHost project template into
    /// <paramref name="outputPath"/>. The output directory is created if it does
    /// not already exist; existing files are overwritten.
    /// </summary>
    public static async Task RenderAsync(
        string outputPath,
        string projectName,
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
        var ports = AppHostProfilePortGenerator.Generate(Random.Shared);
        var userSecretsId = Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture);
        var hostName = ComputeLocalhostTldHostName(projectName);

        // Symbol VALUES are computed here in C# (ports, GUID, derived host name,
        // versions); the template.json manifest only declares which literal in the
        // template tree maps to which of these symbols. Computation stays in code,
        // declaration stays in JSON.
        var symbols = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["projectName"] = projectName,
            ["aspireVersion"] = aspireVersion,
            ["userSecretsId"] = userSecretsId,
            ["hostName"] = hostName,
            ["httpPort"] = ports.DashboardHttpPort.ToString(CultureInfo.InvariantCulture),
            ["httpsPort"] = ports.DashboardHttpsPort.ToString(CultureInfo.InvariantCulture),
            ["otlpHttpPort"] = ports.OtlpHttpPort.ToString(CultureInfo.InvariantCulture),
            ["otlpHttpsPort"] = ports.OtlpHttpsPort.ToString(CultureInfo.InvariantCulture),
            ["resourceHttpPort"] = ports.ResourceServiceHttpPort.ToString(CultureInfo.InvariantCulture),
            ["resourceHttpsPort"] = ports.ResourceServiceHttpsPort.ToString(CultureInfo.InvariantCulture)
        };

        var conditions = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["localhostTld"] = useLocalhostTld
        };

        logger.LogDebug(
            "Rendering embedded C# AppHost project template to '{OutputPath}' (project '{ProjectName}', Aspire version '{AspireVersion}').",
            outputPath,
            projectName,
            aspireVersion);

        if (!Directory.Exists(outputPath))
        {
            Directory.CreateDirectory(outputPath);
        }

        var source = new EmbeddedResourceTemplateSource(typeof(EmbeddedCSharpAppHostTemplate).Assembly, "csharp-apphost");
        var renderer = new ManifestTemplateRenderer(logger);
        await renderer.RenderAsync(source, outputPath, symbols, conditions, cancellationToken);
    }

    /// <summary>
    /// Reproduces the <c>lowerCaseInvariantWithHyphens</c> form chain from the
    /// standalone <c>aspire-apphost</c> template's <c>hostName</c> derived
    /// symbol: lower-case invariant, replace every non-<c>[a-z0-9-]</c> char
    /// with <c>-</c>, collapse repeated hyphens, trim leading and trailing
    /// hyphens. The result is used as the DNS-safe prefix for the
    /// <c>.dev.localhost</c> TLD when the user opts into it.
    /// </summary>
    internal static string ComputeLocalhostTldHostName(string projectName)
    {
        // Example: "My App.Tests" → "my-app-tests".
        var lowered = projectName.ToLowerInvariant();
        var buffer = new StringBuilder(lowered.Length);
        foreach (var c in lowered)
        {
            if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-')
            {
                buffer.Append(c);
            }
            else
            {
                buffer.Append('-');
            }
        }

        // Collapse runs of hyphens.
        var collapsed = new StringBuilder(buffer.Length);
        var previousHyphen = false;
        for (var i = 0; i < buffer.Length; i++)
        {
            var c = buffer[i];
            if (c == '-')
            {
                if (!previousHyphen)
                {
                    collapsed.Append(c);
                }

                previousHyphen = true;
            }
            else
            {
                collapsed.Append(c);
                previousHyphen = false;
            }
        }

        return collapsed.ToString().Trim('-');
    }
}
