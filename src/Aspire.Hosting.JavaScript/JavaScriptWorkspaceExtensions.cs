// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREDOCKERFILEBUILDER001

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.JavaScript;
using Aspire.Hosting.JavaScript.Internal;
using Aspire.Hosting.JavaScript.Internal.Workspace;
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
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="rootPath"/> is null or empty (an API contract violation).
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when this method is called more than once on the same resource — the user
    /// contradicted themselves in code by declaring two different workspace roots.
    /// </exception>
    /// <remarks>
    /// Workspace state is validated at publish time (during <c>aspire publish</c> /
    /// <c>aspire do build</c>) and at run-mode startup (during <c>dotnet run</c> on the AppHost).
    /// Misconfigured workspaces — missing or malformed <c>package.json</c>, no lockfile, the app not
    /// being declared as a member, etc. — surface there as a single
    /// <see cref="DistributedApplicationException"/> listing every problem found, rather than as
    /// exceptions thrown from this method. The intent is that "my repo configuration is wrong" feels
    /// like an Aspire pipeline / run-mode error, not like a bug in the AppHost code.
    /// </remarks>
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
    [Experimental("ASPIREJAVASCRIPT001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    [AspireExport(Description = "Marks the JavaScript application as a member of a workspace and specifies the workspace root path.")]
    public static IResourceBuilder<TResource> WithWorkspaceRoot<TResource>(this IResourceBuilder<TResource> builder, string rootPath)
        where TResource : JavaScriptAppResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(rootPath);

        var resource = builder.Resource;

        if (resource.TryGetLastAnnotation<JavaScriptWorkspaceConfigAnnotation>(out var existing))
        {
            throw new InvalidOperationException(
                $"Resource '{resource.Name}' already has a workspace root '{existing.RootPath}'. " +
                $"{nameof(WithWorkspaceRoot)} can only be called once per resource.");
        }

        // Resolve the path. This is pure path arithmetic — no I/O, no validation. The validator runs
        // later (publish-mode Dockerfile generation, run-mode installer startup) and is responsible
        // for surfacing all configuration errors as a DistributedApplicationException.
        var resolvedRoot = PathNormalizer.NormalizePathForCurrentPlatform(
            Path.GetFullPath(rootPath, builder.ApplicationBuilder.AppHostDirectory));
        var resolvedAppDir = PathNormalizer.NormalizePathForCurrentPlatform(
            Path.GetFullPath(resource.WorkingDirectory));

        builder.WithAnnotation(new JavaScriptWorkspaceConfigAnnotation(resolvedRoot, resolvedAppDir));

        // If a package manager has already been configured (e.g. AddViteApp auto-attaches WithNpm),
        // re-evaluate the install command using the workspace root so 'npm ci' / pnpm 'frozen-lockfile'
        // etc. correctly reflect the lockfile location. This is a best-effort I/O read — if files
        // aren't there yet, the reconfigurer falls back to defaults; the validator catches any actual
        // problem.
        if (resource.TryGetLastAnnotation<JavaScriptPackageManagerAnnotation>(out var pm))
        {
            WorkspacePackageManagerReconfigurer.Reconfigure(builder, pm.ExecutableName, resolvedRoot);
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
        }

        return builder;
    }
}
