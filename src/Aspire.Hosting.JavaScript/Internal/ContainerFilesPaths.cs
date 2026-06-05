// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Builds the <c>SourcePath</c> values used by ContainerFilesSourceAnnotation
// and rewrites annotations attached before WithWorkspaceRoot was called.
//
// In single-app mode the container layout puts every app file under <c>/app</c>.
// In workspace mode the same files live under <c>/app/&lt;AppRelativePath&gt;</c>
// because the Docker build context is the workspace root and the app is a
// member directory beneath it. ContainerFilesSourceAnnotation can be captured
// before <c>WithWorkspaceRoot</c> runs (e.g. when <c>PublishAsStaticWebsite</c>
// is configured first), so existing annotations get rewritten on a best-effort
// basis.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.JavaScript.Internal;

internal static class ContainerFilesPaths
{
    private const string ContainerAppRoot = "/app";

    /// <summary>
    /// Maps an output sub-path (e.g. <c>"dist"</c>) to its in-container path
    /// for a single-app deployment: <c>/app[/dist]</c>.
    /// </summary>
    public static string ForApp(string outputPath)
    {
        var normalized = JavaScriptResourceValidation.NormalizeRelativePath(outputPath);
        return string.IsNullOrEmpty(normalized) || normalized == "."
            ? ContainerAppRoot
            : $"{ContainerAppRoot}/{normalized}";
    }

    /// <summary>
    /// Maps an output sub-path to its in-container path inside the workspace
    /// member directory: <c>/app/&lt;AppRelativePath&gt;[/&lt;output&gt;]</c>.
    /// </summary>
    public static string ForWorkspace(string outputPath, JavaScriptWorkspaceAnnotation workspace)
    {
        var normalized = JavaScriptResourceValidation.NormalizeRelativePath(outputPath);
        var basePath = $"{ContainerAppRoot}/{workspace.AppRelativePath}";
        return string.IsNullOrEmpty(normalized) || normalized == "."
            ? basePath
            : $"{basePath}/{normalized}";
    }

    /// <summary>
    /// Rewrites <see cref="ContainerFilesSourceAnnotation"/> entries that point
    /// at <c>/app[/...]</c> so they instead point at
    /// <c>/app/&lt;AppRelativePath&gt;[/...]</c>. Called by
    /// <c>WithWorkspaceRoot</c> when annotations were captured by an earlier
    /// <c>PublishAsStaticWebsite</c> / <c>PublishAsNodeServer</c> call.
    /// </summary>
    public static void RewriteForWorkspace(IResource resource, JavaScriptWorkspaceAnnotation workspace)
    {
        var existing = resource.Annotations.OfType<ContainerFilesSourceAnnotation>().ToList();
        if (existing.Count == 0)
        {
            return;
        }

        var prefix = $"{ContainerAppRoot}/{workspace.AppRelativePath}";
        foreach (var ann in existing)
        {
            var src = ann.SourcePath;
            string newSrc;
            if (src == ContainerAppRoot)
            {
                newSrc = prefix;
            }
            else if (src.StartsWith($"{ContainerAppRoot}/", StringComparison.Ordinal))
            {
                newSrc = $"{prefix}/{src.Substring($"{ContainerAppRoot}/".Length)}";
            }
            else
            {
                continue;
            }

            resource.Annotations.Remove(ann);
            resource.Annotations.Add(new ContainerFilesSourceAnnotation { SourcePath = newSrc });
        }
    }
}
