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

        var conditions = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["localhostTld"] = useLocalhostTld
        };

        string ApplyContentTransform(string content)
        {
            var tokensApplied = ApplyTokens(content, projectName, aspireVersion, ports, hostName, userSecretsId);
            return ConditionalBlockProcessor.Process(tokensApplied, conditions);
        }

        // Conditional blocks in paths are nonsensical (they require multi-line
        // marker pairs) — restrict the path transform to plain token replacement
        // so a file or directory named e.g. `{{projectName}}._csproj` becomes
        // `<projectName>.csproj` without the conditional pass throwing on an
        // unmatched start marker.
        //
        // Embedded source files use the `._csproj` extension instead of `.csproj`
        // so the repo-wide MSBuild traversal (eng/Build.props) does not pick them
        // up as real projects and try to resolve the unsubstituted
        // `Aspire.AppHost.Sdk/{{aspireVersion}}` reference. The path transform
        // restores `.csproj` for the rendered output.
        string ApplyPathTransform(string segment)
        {
            var tokensApplied = ApplyTokens(segment, projectName, aspireVersion, ports, hostName, userSecretsId);
            return RewriteTemplateProjectExtension(tokensApplied);
        }

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
        var renderer = new TemplateRenderer(logger);
        await renderer.RenderAsync(source, outputPath, ApplyContentTransform, cancellationToken, ApplyPathTransform);
    }

    // Source files in the embedded template tree use the `._csproj` extension
    // so the repo-wide MSBuild traversal (eng/Build.props) does not match them.
    // Rewrite to `.csproj` (and the analogous `._fsproj` / `._vbproj` shapes for
    // forward compatibility) only on the trailing segment of the path so a
    // directory that happens to contain that token is not affected.
    private static string RewriteTemplateProjectExtension(string segment)
    {
        if (segment.EndsWith("._csproj", StringComparison.Ordinal))
        {
            return string.Concat(segment.AsSpan(0, segment.Length - "._csproj".Length), ".csproj");
        }

        if (segment.EndsWith("._fsproj", StringComparison.Ordinal))
        {
            return string.Concat(segment.AsSpan(0, segment.Length - "._fsproj".Length), ".fsproj");
        }

        if (segment.EndsWith("._vbproj", StringComparison.Ordinal))
        {
            return string.Concat(segment.AsSpan(0, segment.Length - "._vbproj".Length), ".vbproj");
        }

        return segment;
    }

    private static string ApplyTokens(
        string content,
        string projectName,
        string aspireVersion,
        AppHostProfilePorts ports,
        string hostName,
        string userSecretsId)
    {
        return content
            .Replace("{{projectName}}", projectName)
            .Replace("{{aspireVersion}}", aspireVersion)
            .Replace("{{userSecretsId}}", userSecretsId)
            .Replace("{{hostName}}", hostName)
            .Replace("{{httpPort}}", ports.DashboardHttpPort.ToString(CultureInfo.InvariantCulture))
            .Replace("{{httpsPort}}", ports.DashboardHttpsPort.ToString(CultureInfo.InvariantCulture))
            .Replace("{{otlpHttpPort}}", ports.OtlpHttpPort.ToString(CultureInfo.InvariantCulture))
            .Replace("{{otlpHttpsPort}}", ports.OtlpHttpsPort.ToString(CultureInfo.InvariantCulture))
            .Replace("{{resourceHttpPort}}", ports.ResourceServiceHttpPort.ToString(CultureInfo.InvariantCulture))
            .Replace("{{resourceHttpsPort}}", ports.ResourceServiceHttpsPort.ToString(CultureInfo.InvariantCulture));
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
