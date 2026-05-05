// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREDOCKERFILEBUILDER001

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.JavaScript;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting;

/// <summary>
/// Workspace-related extensions for JavaScript application resources.
/// </summary>
public static class JavaScriptWorkspaceExtensions
{
    /// <summary>
    /// Marks the JavaScript application as a member of a JavaScript workspace (yarn / npm / pnpm / bun
    /// monorepo) rooted at <paramref name="rootPath"/>. In publish mode, the auto-generated Dockerfile
    /// uses the workspace root as its build context, copies workspace-level manifests, runs install at the
    /// root, and uses the configured package manager's native workspace filter to build and start this
    /// resource.
    /// </summary>
    /// <typeparam name="TResource">The JavaScript application resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="rootPath">
    /// Path to the workspace root directory. Resolved against the AppHost's directory when relative.
    /// The directory must contain either a <c>package.json</c> with a <c>workspaces</c> field, or a
    /// <c>pnpm-workspace.yaml</c>.
    /// </param>
    /// <returns>The resource builder for chaining.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="rootPath"/> is null or empty.</exception>
    /// <exception cref="DistributedApplicationException">
    /// Thrown when the workspace configuration is invalid: the root does not exist, the application
    /// directory is not a descendant of the root, the application directory is not a declared workspace
    /// member, the application's <c>package.json</c> is missing or has no <c>name</c> field, no lockfile
    /// is present at the workspace root, or the workspace declaration uses unsupported pattern syntax
    /// (for example, negated patterns).
    /// </exception>
    /// <example>
    /// Configure a Vite app inside a pnpm workspace:
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// builder.AddViteApp("web", "../monorepo/packages/web")
    ///        .WithWorkspaceRoot("../monorepo")
    ///        .WithPnpm();
    ///
    /// builder.Build().Run();
    /// </code>
    /// </example>
    [AspireExport(Description = "Marks the JavaScript application as a member of a workspace and specifies the workspace root path.")]
    public static IResourceBuilder<TResource> WithWorkspaceRoot<TResource>(this IResourceBuilder<TResource> builder, string rootPath)
        where TResource : JavaScriptAppResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(rootPath);

        var resource = builder.Resource;
        var appDir = PathNormalizer.NormalizePathForCurrentPlatform(Path.GetFullPath(resource.WorkingDirectory));
        var resolvedRoot = PathNormalizer.NormalizePathForCurrentPlatform(
            Path.GetFullPath(rootPath, builder.ApplicationBuilder.AppHostDirectory));

        if (!Directory.Exists(resolvedRoot))
        {
            throw new DistributedApplicationException(
                $"Workspace root '{resolvedRoot}' does not exist. Pass an existing directory to {nameof(WithWorkspaceRoot)}.");
        }

        var rootFull = PathWithSeparator(resolvedRoot);
        var appFull = PathWithSeparator(appDir);
        if (!appFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) || appFull.Length == rootFull.Length)
        {
            throw new DistributedApplicationException(
                $"Application directory '{appDir}' is not a descendant of workspace root '{resolvedRoot}'.");
        }

        var appRelative = appFull[rootFull.Length..].TrimEnd(Path.DirectorySeparatorChar).Replace(Path.DirectorySeparatorChar, '/');
        if (string.IsNullOrEmpty(appRelative))
        {
            throw new DistributedApplicationException(
                $"Application directory '{appDir}' is the workspace root; expected a workspace member subdirectory.");
        }

        var appName = JavaScriptWorkspaceReader.TryReadPackageName(appDir);
        if (string.IsNullOrEmpty(appName))
        {
            throw new DistributedApplicationException(
                $"Application '{resource.Name}' must have a package.json with a non-empty 'name' field at '{appDir}' to be used in workspace mode.");
        }

        var rawPatterns = JavaScriptWorkspaceReader.ReadWorkspacePatterns(resolvedRoot);
        if (rawPatterns.Count == 0)
        {
            throw new DistributedApplicationException(
                $"No workspace patterns declared at '{resolvedRoot}'. Expected a 'workspaces' field in package.json or a 'packages' list in pnpm-workspace.yaml.");
        }

        foreach (var pattern in rawPatterns)
        {
            if (pattern.StartsWith('!'))
            {
                throw new DistributedApplicationException(
                    $"Negated workspace pattern '{pattern}' at '{resolvedRoot}' is not supported.");
            }
            if (pattern.Contains("**", StringComparison.Ordinal))
            {
                throw new DistributedApplicationException(
                    $"Recursive workspace pattern '{pattern}' at '{resolvedRoot}' is not supported.");
            }
        }

        var workspaceDirs = JavaScriptWorkspaceReader.ExpandWorkspacePatterns(resolvedRoot, rawPatterns);
        if (!workspaceDirs.Contains(appRelative, StringComparer.OrdinalIgnoreCase))
        {
            throw new DistributedApplicationException(
                $"Application directory '{appRelative}' is not a declared workspace member of '{resolvedRoot}'. Declared members: {string.Join(", ", workspaceDirs)}");
        }

        var rootFiles = new List<string>();
        var hasLockfile = false;
        foreach (var lockName in s_lockfileNames)
        {
            if (File.Exists(Path.Combine(resolvedRoot, lockName)))
            {
                rootFiles.Add(lockName);
                hasLockfile = true;
            }
        }
        if (File.Exists(Path.Combine(resolvedRoot, "package.json")))
        {
            rootFiles.Insert(0, "package.json");
        }
        else
        {
            throw new DistributedApplicationException(
                $"Workspace root '{resolvedRoot}' is missing package.json.");
        }
        foreach (var optional in s_optionalRootManifestFiles)
        {
            if (File.Exists(Path.Combine(resolvedRoot, optional)))
            {
                rootFiles.Add(optional);
            }
        }

        var rootDirs = new List<string>();
        if (Directory.Exists(Path.Combine(resolvedRoot, ".yarn")))
        {
            rootDirs.Add(".yarn");
        }

        if (!hasLockfile)
        {
            throw new DistributedApplicationException(
                $"Workspace root '{resolvedRoot}' has no recognized lockfile (expected one of: {string.Join(", ", s_lockfileNames)}).");
        }

        var annotation = new JavaScriptWorkspaceAnnotation(
            resolvedRoot,
            appName,
            appRelative,
            workspaceDirs,
            rootFiles,
            rootDirs);

        builder.WithAnnotation(annotation, ResourceAnnotationMutationBehavior.Replace);

        // If a package manager has already been configured (e.g. AddViteApp auto-attaches WithNpm),
        // re-evaluate the install command using the workspace root so 'npm ci' / pnpm 'frozen-lockfile'
        // etc. correctly reflect the lockfile location.
        if (resource.TryGetLastAnnotation<JavaScriptPackageManagerAnnotation>(out var pm))
        {
            JavaScriptHostingExtensions.ReconfigurePackageManagerForWorkspace(builder, pm.ExecutableName, resolvedRoot);
        }

        // PublishAsDockerFile invokes its configure callback eagerly at registration time. When
        // WithWorkspaceRoot is called after PublishAsDockerFile (the typical case for AddNodeApp /
        // AddViteApp / AddNextJsApp where PublishAsDockerFile is wired up internally), the
        // DockerfileBuildAnnotation has already been created with the application directory as its
        // context path. We need to swap that for the workspace root so Docker actually sees the
        // root-level package.json and lockfile.
        if (resource.TryGetLastAnnotation<DockerfileBuildAnnotation>(out var oldDockerfileAnnotation))
        {
            var newAnnotation = new DockerfileBuildAnnotation(
                contextPath: resolvedRoot,
                dockerfilePath: oldDockerfileAnnotation.DockerfilePath,
                stage: oldDockerfileAnnotation.Stage)
            {
                DockerfileFactory = oldDockerfileAnnotation.DockerfileFactory,
                ImageName = oldDockerfileAnnotation.ImageName,
                ImageTag = oldDockerfileAnnotation.ImageTag,
                HasEntrypoint = oldDockerfileAnnotation.HasEntrypoint,
            };
            foreach (var kvp in oldDockerfileAnnotation.BuildArguments)
            {
                newAnnotation.BuildArguments[kvp.Key] = kvp.Value;
            }
            foreach (var kvp in oldDockerfileAnnotation.BuildSecrets)
            {
                newAnnotation.BuildSecrets[kvp.Key] = kvp.Value;
            }
            builder.WithAnnotation(newAnnotation, ResourceAnnotationMutationBehavior.Replace);

            JavaScriptHostingExtensions.RewriteContainerFilesSourcesForWorkspace(resource, annotation);
        }

        return builder;
    }

    private static string PathWithSeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;
    }

    private static readonly string[] s_lockfileNames =
    [
        "package-lock.json",
        "npm-shrinkwrap.json",
        "yarn.lock",
        "pnpm-lock.yaml",
        "bun.lock",
        "bun.lockb",
    ];

    private static readonly string[] s_optionalRootManifestFiles =
    [
        "pnpm-workspace.yaml",
        ".yarnrc.yml",
        ".yarnrc",
        ".npmrc",
        "bunfig.toml",
    ];
}
