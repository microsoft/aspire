// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.Azure.Azd;

/// <summary>
/// Locates an azd project's existing infrastructure-as-code folder and classifies its provider, mirroring
/// azd's own resolution so both the runtime importer and the code generator preserve the same assets.
/// </summary>
/// <remarks>
/// azd defaults to an <c>infra</c> folder containing a <c>main</c> module, and auto-detects the provider from
/// the file extensions present when it is not pinned in <c>azure.yaml</c>: <c>.bicep</c>/<c>.bicepparam</c>
/// imply Bicep and <c>.tf</c>/<c>.tfvars</c>(<c>.json</c>) imply Terraform.
/// See <see href="https://github.com/Azure/azure-dev/blob/main/cli/azd/pkg/project/importer.go"/>.
/// </remarks>
internal static class AzdInfrastructureLocator
{
    /// <summary>
    /// Attempts to locate the project's infrastructure folder.
    /// </summary>
    /// <param name="projectDirectory">The directory that contains <c>azure.yaml</c>.</param>
    /// <param name="project">The parsed azd project (its <c>infra</c> block, if any, overrides the defaults).</param>
    /// <param name="infraPath">When found, the absolute path to the infrastructure folder.</param>
    /// <param name="provider">When found, the detected provider (<c>bicep</c> or <c>terraform</c>).</param>
    /// <param name="relativePath">When found, the infrastructure path relative to the project (as authored).</param>
    /// <returns><see langword="true"/> if an infrastructure folder exists; otherwise <see langword="false"/>.</returns>
    public static bool TryLocate(
        string projectDirectory,
        AzdProject project,
        [NotNullWhen(true)] out string? infraPath,
        [NotNullWhen(true)] out string? provider,
        [NotNullWhen(true)] out string? relativePath)
    {
        infraPath = null;
        provider = null;
        relativePath = project.Infra?.Path ?? "infra";

        var candidate = Path.GetFullPath(Path.Combine(projectDirectory, relativePath));
        if (!Directory.Exists(candidate))
        {
            relativePath = null;
            return false;
        }

        infraPath = candidate;
        provider = project.Infra?.Provider ?? DetectProvider(candidate);
        return true;
    }

    private static string DetectProvider(string infraPath)
    {
        // Best-effort classification only; the folder is referenced, not parsed. Match azd's extension scan.
        var hasBicep = Directory.EnumerateFiles(infraPath, "*.bicep", SearchOption.AllDirectories).Any()
            || Directory.EnumerateFiles(infraPath, "*.bicepparam", SearchOption.AllDirectories).Any();
        var hasTerraform = Directory.EnumerateFiles(infraPath, "*.tf", SearchOption.AllDirectories).Any()
            || Directory.EnumerateFiles(infraPath, "*.tfvars", SearchOption.AllDirectories).Any()
            || Directory.EnumerateFiles(infraPath, "*.tf.json", SearchOption.AllDirectories).Any();

        // When only Terraform files are present, report terraform; otherwise default to bicep (azd's default,
        // and the provider Aspire understands).
        return hasTerraform && !hasBicep ? "terraform" : "bicep";
    }
}
