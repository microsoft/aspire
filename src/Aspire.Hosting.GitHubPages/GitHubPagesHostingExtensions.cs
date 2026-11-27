// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREGITHUBPAGES001
#pragma warning disable ASPIRECONTAINERRUNTIME001
#pragma warning disable ASPIREFILESYSTEM001

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.GitHubPages;
using Aspire.Hosting.JavaScript;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Publishing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for modeling GitHub Pages static website environments.
/// </summary>
[Experimental("ASPIREGITHUBPAGES001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public static class GitHubPagesHostingExtensions
{
    /// <summary>
    /// Adds a publish-only GitHub Pages environment for a single static website.
    /// </summary>
    /// <param name="builder">The application builder.</param>
    /// <param name="name">The Aspire resource name of the environment.</param>
    /// <param name="repository">The destination repository on github.com in owner/name form.</param>
    /// <returns>The environment builder.</returns>
    /// <remarks>
    /// Assign a Vite app using <c>WithComputeEnvironment</c>. Publishing builds and extracts its
    /// static files without Pages credentials. This integration does not upload or deploy the site.
    /// The declared site address must match the repository's existing Pages configuration.
    /// Private enterprise sites and inherited account custom domains are not automatically discovered.
    /// </remarks>
    /// <example>
    /// <code>
    /// var pages = builder.AddGitHubPagesEnvironment("pages", "owner/repository");
    /// builder.AddViteApp("frontend", "../frontend").WithComputeEnvironment(pages);
    /// </code>
    /// </example>
    /// <exception cref="ArgumentNullException">Thrown when the builder is null.</exception>
    /// <exception cref="ArgumentException">Thrown when the name or repository is invalid.</exception>
    [AspireExport]
    public static IResourceBuilder<GitHubPagesEnvironmentResource> AddGitHubPagesEnvironment(
        this IDistributedApplicationBuilder builder, [ResourceName] string name, string repository)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var environment = new GitHubPagesEnvironmentResource(name, repository);
        if (builder.ExecutionContext.IsRunMode)
        {
            return builder.CreateResourceBuilder(environment);
        }

        return builder.AddResource(environment)
            .WithPipelineStepFactory(_ =>
            {
                var steps = new List<PipelineStep>
                {
                    new()
                    {
                        Name = $"prepare-github-pages-{name}",
                        Description = $"Prepares static website targets for GitHub Pages environment '{name}'.",
                        DependsOnSteps = [WellKnownPipelineSteps.ValidateComputeEnvironments],
                        RequiredBySteps = [WellKnownPipelineSteps.BeforeStart],
                        Action = ctx =>
                        {
                            PrepareTarget(environment, ctx.Model);
                            return Task.CompletedTask;
                        }
                    },
                    new()
                    {
                        Name = $"validate-github-pages-deployment-{name}",
                        Description = "Rejects deployment until a GitHub Pages publisher is available.",
                        RequiredBySteps = [WellKnownPipelineSteps.Deploy, WellKnownPipelineSteps.DeployPrereq],
                        Action = _ => throw new DistributedApplicationException(
                            "This GitHub Pages spike generates bundles only. Use aspire publish; GitHub Pages deployment is not implemented.")
                    }
                };

                if (environment.Site is { } site)
                {
                    steps.Add(new PipelineStep
                    {
                        Name = site.BundleStepName,
                        Description = $"Writes the static website bundle for '{site.Source.Name}'.",
                        Resource = site,
                        DependsOnSteps = [WellKnownPipelineSteps.PublishPrereq],
                        RequiredBySteps = [WellKnownPipelineSteps.Publish],
                        Action = ctx => WriteBundleAsync(site, ctx)
                    });
                }

                return steps;
            })
            .WithPipelineConfiguration(context =>
            {
                if (environment.Site is not { } site)
                {
                    return;
                }

                var bundleStep = context.Steps.Single(step => step.Name == site.BundleStepName);
                var buildSteps = context.GetSteps(site.Source, WellKnownPipelineTags.BuildCompute).ToArray();
                if (site.Source.RequiresImageBuild() && buildSteps.Length == 0)
                {
                    throw new DistributedApplicationException($"Static website source '{site.Source.Name}' has no image build step.");
                }
                foreach (var buildStep in buildSteps)
                {
                    bundleStep.DependsOn(buildStep.Name);
                }
            });
    }

    /// <summary>
    /// Declares the canonical custom domain configured for a GitHub Pages site.
    /// </summary>
    /// <param name="builder">The GitHub Pages environment builder.</param>
    /// <param name="domain">The DNS hostname, without a scheme, port, or path.</param>
    /// <returns>The environment builder.</returns>
    /// <remarks>
    /// The content base path becomes <c>/</c>. This method does not change Pages settings, DNS,
    /// certificates, or CORS permissions, and does not declare additional redirect aliases.
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown when the builder is null.</exception>
    /// <exception cref="ArgumentException">Thrown when the domain is not a DNS hostname.</exception>
    [AspireExport]
    public static IResourceBuilder<GitHubPagesEnvironmentResource> WithCustomDomain(
        this IResourceBuilder<GitHubPagesEnvironmentResource> builder, string domain)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);
        if (domain.Length > 253 || !domain.Contains('.') ||
            Uri.CheckHostName(domain) != UriHostNameType.Dns || domain.EndsWith('.') ||
            domain.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '.') ||
            domain.Split('.').Any(label => label.Length is 0 or > 63 || label.StartsWith('-') || label.EndsWith('-')))
        {
            throw new ArgumentException("The custom domain must be a DNS hostname without a scheme, port, or path.", nameof(domain));
        }

        builder.Resource.CustomDomain = domain.ToLowerInvariant();
        return builder;
    }

    internal static void PrepareTarget(GitHubPagesEnvironmentResource environment, DistributedApplicationModel model)
    {
        if (model.Resources.OfType<GitHubPagesEnvironmentResource>().Count(
            candidate => string.Equals(candidate.Repository, environment.Repository, StringComparison.OrdinalIgnoreCase)) > 1)
        {
            throw new DistributedApplicationException($"Multiple GitHub Pages environments target repository '{environment.Repository}'. A repository hosts only one Pages site.");
        }

        var isDefault = model.Resources.OfType<IComputeEnvironmentResource>().Count() == 1;
        // GetComputeResources deliberately omits build-only images; those are exactly the
        // workloads a static environment consumes. Select explicit bindings from the full model.
        var sources = model.Resources.OfType<IComputeResource>().Where(source =>
            !source.IsExcludedFromPublish() &&
            (ReferenceEquals(source.GetComputeEnvironment(), environment) ||
             (source.GetComputeEnvironment() is null && isDefault))).ToArray();
        if (sources.Length > 1)
        {
            throw new DistributedApplicationException($"GitHub Pages environment '{environment.Name}' can host only one static website. Bind other workloads to a different compute environment.");
        }
        if (sources.Length == 0)
        {
            throw new DistributedApplicationException($"GitHub Pages environment '{environment.Name}' has no static website. Assign a resource using WithComputeEnvironment.");
        }

        var source = sources[0];
        var paths = source.Annotations.OfType<ContainerFilesSourceAnnotation>().ToArray();
        if (paths.Length != 1 || !paths[0].SourcePath.StartsWith('/') ||
            paths[0].SourcePath.Contains('\\') || paths[0].SourcePath.Contains('\0') ||
            paths[0].SourcePath.Split('/').Contains(".."))
        {
            throw new DistributedApplicationException($"Static website source '{source.Name}' must declare exactly one absolute container files directory without parent traversal.");
        }
        if (!source.TryGetContainerImageName(out _))
        {
            throw new DistributedApplicationException($"Static website source '{source.Name}' must provide a container image.");
        }

        if (environment.Site is not null)
        {
            if (!ReferenceEquals(environment.Site.Source, source))
            {
                throw new DistributedApplicationException($"The static website source for GitHub Pages environment '{environment.Name}' changed after preparation.");
            }
            return;
        }

        if (source.HasAnnotationOfType<ViteAppAnnotation>())
        {
            if (!source.TryGetLastAnnotation<DockerfileBuildAnnotation>(out var dockerfile))
            {
                throw new DistributedApplicationException($"Vite app '{source.Name}' must publish build-only static files, not a server or SSR container.");
            }
            if (dockerfile.DockerfileFactory is null)
            {
                throw new DistributedApplicationException($"Vite app '{source.Name}' uses a custom Dockerfile. Supply static container files from a separate build resource with its base path configured explicitly.");
            }
            if (dockerfile.HasEntrypoint)
            {
                throw new DistributedApplicationException($"Vite app '{source.Name}' must publish build-only static files, not a server or SSR container.");
            }
            ConfigureViteBasePath(source, environment.BasePath);
        }

        var target = new GitHubPagesSiteResource(source, environment);
        source.Annotations.Add(new DeploymentTargetAnnotation(target) { ComputeEnvironment = environment });
        // Keep the standard file-consumer edge on the environment, which is in the model.
        // Deployment targets are metadata-only and are not enumerated by build-only validation.
        environment.Annotations.Add(new ContainerFilesDestinationAnnotation { Source = source, DestinationPath = "/" });
        environment.Site = target;
    }

    private static void ConfigureViteBasePath(IResource source, string basePath)
    {
        if (!source.TryGetLastAnnotation<JavaScriptBuildScriptAnnotation>(out var script) ||
            !source.TryGetLastAnnotation<JavaScriptPackageManagerAnnotation>(out var packageManager))
        {
            throw new DistributedApplicationException($"Vite app '{source.Name}' has no package-manager build script.");
        }
        if (script.Args.Any(arg => arg == "--base" || arg.StartsWith("--base=", StringComparison.Ordinal)))
        {
            throw new DistributedApplicationException($"Vite app '{source.Name}' configures --base in its build arguments. GitHub Pages controls the base path; remove that build argument.");
        }

        var args = script.Args.ToList();
        if (packageManager.CommandSeparator is { } separator && !args.Contains(separator))
        {
            args.Insert(0, separator);
        }
        args.Add($"--base={basePath}");
        source.Annotations.Add(new JavaScriptBuildScriptAnnotation(script.ScriptName, args.ToArray()));
    }

    private static async Task WriteBundleAsync(GitHubPagesSiteResource site, PipelineStepContext context)
    {
        var outputPath = context.Services.GetRequiredService<IOptions<PipelineOptions>>().Value.OutputPath;
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            throw new DistributedApplicationException("GitHub Pages bundle generation requires a pipeline output path.");
        }
        var sourcePath = site.Source.Annotations.OfType<ContainerFilesSourceAnnotation>().Single().SourcePath;
        if (!site.Source.TryGetContainerImageName(out var imageName))
        {
            throw new DistributedApplicationException($"Static website source '{site.Source.Name}' has no container image.");
        }

        var runtime = await context.Services.GetRequiredService<IContainerRuntimeResolver>().ResolveAsync(context.CancellationToken).ConfigureAwait(false);
        using var staging = context.Services.GetRequiredService<IFileSystemService>().TempDirectory.CreateTempSubdirectory("github-pages");
        await runtime.CopyFilesFromImageAsync(imageName, sourcePath, staging.Path, context.CancellationToken).ConfigureAwait(false);
        ValidateBundle(staging.Path, context.CancellationToken);

        var outputDirectory = Path.GetFullPath(Path.Combine(outputPath, "github-pages"));
        var directory = Path.Combine(outputDirectory, site.Environment.Name);
        var bundlePath = Path.Combine(directory, "dist");
        Directory.CreateDirectory(outputDirectory);
        if (Directory.Exists(directory) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
        {
            throw new DistributedApplicationException($"GitHub Pages output directory '{directory}' cannot be a symbolic link.");
        }

        var metadata = new
        {
            repository = site.Environment.Repository,
            source = site.Source.Name,
            url = site.Environment.SiteUrl.AbsoluteUri,
            origin = site.Environment.Origin,
            basePath = site.Environment.BasePath,
            bundle = "dist"
        };
        // Prepare on the output filesystem so replacing an existing bundle only requires
        // directory renames. Failed extraction, copying, or cancellation keeps the old output.
        var preparedDirectory = Path.Combine(outputDirectory, $".{Path.GetFileName(staging.Path)}");
        var previousDirectory = $"{preparedDirectory}.previous";
        try
        {
            CopyDirectory(staging.Path, Path.Combine(preparedDirectory, "dist"), context.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(preparedDirectory, "site.json"),
                JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine,
                context.CancellationToken).ConfigureAwait(false);
            context.CancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(directory))
            {
                Directory.Move(directory, previousDirectory);
            }
            try
            {
                Directory.Move(preparedDirectory, directory);
            }
            catch
            {
                if (Directory.Exists(previousDirectory))
                {
                    Directory.Move(previousDirectory, directory);
                }
                throw;
            }
            if (Directory.Exists(previousDirectory))
            {
                Directory.Delete(previousDirectory, recursive: true);
            }
        }
        finally
        {
            if (Directory.Exists(preparedDirectory))
            {
                Directory.Delete(preparedDirectory, recursive: true);
            }
        }
        context.Logger.LogInformation("Static website bundle for {ResourceName} written to {BundlePath}.", site.Source.Name, bundlePath);
        context.Summary.Add($"Bundle ({site.Source.Name})", bundlePath);
    }

    internal static void ValidateBundle(string directory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateBundleEntries(new DirectoryInfo(directory), cancellationToken);
        if (!File.Exists(Path.Combine(directory, "index.html")))
        {
            throw new DistributedApplicationException("The static website bundle must contain index.html at its root.");
        }
    }

    private static void ValidateBundleEntries(DirectoryInfo directory, CancellationToken cancellationToken)
    {
        foreach (var entry in directory.EnumerateFileSystemInfos())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new DistributedApplicationException($"GitHub Pages bundles cannot contain symbolic links: '{entry.Name}'.");
            }
            if (entry is DirectoryInfo child)
            {
                ValidateBundleEntries(child, cancellationToken);
            }
        }
    }

    private static void CopyDirectory(string source, string destination, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }
        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)), cancellationToken);
        }
    }
}
