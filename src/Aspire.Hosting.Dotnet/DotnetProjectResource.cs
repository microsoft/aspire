// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

#pragma warning disable ASPIREPROJECTS001 // ProjectLaunchDefaultsAnnotation is experimental.
#pragma warning disable ASPIREPIPELINES001 // Pipeline APIs are experimental.

namespace Aspire.Hosting.Dotnet;

/// <summary>
/// A resource that represents a specified C# project or file-based app added by path.
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="DotnetProjectResource"/> is added by path and is
/// launched as an executable: <c>dotnet run --project &lt;path&gt;</c> for a project file, or
/// <c>dotnet run --file &lt;path&gt;</c> for a file-based app (a <c>.cs</c> file).
/// </para>
/// <para>
/// Automatic publishing is not supported. Use <c>AddProject&lt;TProject&gt;(...)</c> or
/// <c>AddCSharpApp(...)</c> for standard .NET project publishing, explicitly configure container
/// publishing with <c>PublishAsDockerFile(...)</c>, or exclude an intentionally run-only resource
/// with <c>ExcludeFromManifest()</c>.
/// </para>
/// </remarks>
[Experimental("ASPIREDOTNETPROJECT001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
[AspireExport(ExposeProperties = true)]
public class DotnetProjectResource : ExecutableResource, IResourceWithServiceDiscovery
{
    private const string PublishManifestStepName = "publish-manifest";
    private const string ValidateDotnetProjectPublishStepName = "validate-dotnet-project-publish";
    private static readonly string[] s_publishAndDeployRootStepNames =
    [
        WellKnownPipelineSteps.Publish,
        WellKnownPipelineSteps.PublishPrereq,
        WellKnownPipelineSteps.Deploy,
        WellKnownPipelineSteps.DeployPrereq,
        PublishManifestStepName
    ];
    private static readonly string[] s_publishAndDeployWorkRootStepNames =
    [
        .. s_publishAndDeployRootStepNames,
        WellKnownPipelineSteps.Build,
        WellKnownPipelineSteps.BuildPrereq,
        WellKnownPipelineSteps.Push,
        WellKnownPipelineSteps.PushPrereq
    ];
    private readonly ManifestPublishingCallbackAnnotation _unsupportedPublishCallback;

    /// <summary>
    /// Initializes a new instance of the <see cref="DotnetProjectResource"/> class.
    /// </summary>
    /// <param name="name">The name of the resource in the application model.</param>
    /// <param name="workingDirectory">The working directory for the app, typically the directory containing the project or <c>.cs</c> file.</param>
    public DotnetProjectResource(string name, string workingDirectory) : base(name, "dotnet", workingDirectory)
    {
        // Ensure uniform C# project defaults, including the Rebuild command and Kestrel endpoint wiring.
        Annotations.Add(new ProjectLaunchDefaultsAnnotation());

        _unsupportedPublishCallback = new ManifestPublishingCallbackAnnotation(_ =>
            Task.FromException(new DistributedApplicationException(GetUnsupportedPublishMessage(Name))));

        // PublishAsDockerFile replaces the executable with a container that shares this annotation collection,
        // while ExcludeFromManifest appends the ignore callback and WithManifestPublishingCallback replaces the
        // default callback. Evaluate the effective publisher so explicit choices remain valid and only an
        // untransformed DotnetProjectResource fails.
        Annotations.Add(new PipelineStepAnnotation(context =>
        {
            var unsupportedResources = context.PipelineContext.Model.Resources
                .OfType<DotnetProjectResource>()
                .Where(static resource => resource.RequiresPublishValidation())
                .ToArray();

            if (unsupportedResources.Length == 0 || !ReferenceEquals(context.Resource, unsupportedResources[0]))
            {
                return [];
            }

            return
            [
                new PipelineStep
                {
                    Name = ValidateDotnetProjectPublishStepName,
                    Description = "Validates publish support for .NET project resources.",
                    Action = _ => throw new DistributedApplicationException(
                        GetUnsupportedPublishMessage(unsupportedResources.Select(resource => resource.Name).ToArray())),
                    Resource = unsupportedResources[0]
                }
            ];
        }));

        // Configuration callbacks run in model order, so later compute-environment callbacks may not have attached
        // build and push work to publish or deploy yet. Use those steps' stable aggregation roots to add the
        // validation dependency before any step action can be scheduled.
        Annotations.Add(new PipelineConfigurationAnnotation(context =>
        {
            var validationStep = context.Steps.SingleOrDefault(step =>
                step.Name == ValidateDotnetProjectPublishStepName &&
                step.Resource is DotnetProjectResource resource &&
                resource.RequiresPublishValidation());

            if (validationStep is null)
            {
                return;
            }

            foreach (var step in GetPublishAndDeploySteps(context))
            {
                if (!ReferenceEquals(step, validationStep) &&
                    !step.DependsOnSteps.Contains(ValidateDotnetProjectPublishStepName))
                {
                    step.DependsOn(validationStep);
                }
            }
        }));

        // Prevent legacy manifest publishing from falling back to executable.v0, which would serialize
        // `dotnet run` arguments containing machine-local project paths.
        Annotations.Add(_unsupportedPublishCallback);
    }

    private bool RequiresPublishValidation() =>
        this.TryGetLastAnnotation<ManifestPublishingCallbackAnnotation>(out var effectiveCallback) &&
        ReferenceEquals(effectiveCallback, _unsupportedPublishCallback);

    private static IEnumerable<PipelineStep> GetPublishAndDeploySteps(PipelineConfigurationContext context)
    {
        var selectedStepName = context.Services.GetRequiredService<IOptions<PipelineOptions>>().Value.Step;
        if (!string.IsNullOrWhiteSpace(selectedStepName) &&
            !GetStepsRequiredByRoots(context.Steps, [selectedStepName])
                .Any(step => s_publishAndDeployRootStepNames.Contains(step.Name, StringComparer.Ordinal)))
        {
            return [];
        }

        // Before-start resolves the same pipeline graph before publish execution and can share preparation steps
        // with deploy. Keep that branch usable, while gating build and push work only when the selected target
        // actually reaches publish or deploy.
        var beforeStartStepNames = GetStepsRequiredByRoots(
            context.Steps,
            [WellKnownPipelineSteps.BeforeStart])
            .Select(step => step.Name)
            .ToHashSet(StringComparer.Ordinal);

        return GetStepsRequiredByRoots(context.Steps, s_publishAndDeployWorkRootStepNames, beforeStartStepNames);
    }

    private static IEnumerable<PipelineStep> GetStepsRequiredByRoots(
        IReadOnlyList<PipelineStep> steps,
        IReadOnlyList<string> rootStepNames,
        IReadOnlySet<string>? excludedStepNames = null)
    {
        var stepsByName = steps.ToLookup(step => step.Name, StringComparer.Ordinal);
        var requiredStepsByName = steps
            .SelectMany(step => step.RequiredBySteps.Select(requiredByStepName => (requiredByStepName, step)))
            .ToLookup(item => item.requiredByStepName, item => item.step, StringComparer.Ordinal);
        var pendingSteps = new Queue<PipelineStep>();
        var visitedStepNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var rootStepName in rootStepNames)
        {
            if (stepsByName[rootStepName].FirstOrDefault() is { } rootStep)
            {
                pendingSteps.Enqueue(rootStep);
            }
        }

        while (pendingSteps.TryDequeue(out var step))
        {
            if (!visitedStepNames.Add(step.Name) || excludedStepNames?.Contains(step.Name) is true)
            {
                continue;
            }

            yield return step;

            foreach (var dependencyStepName in step.DependsOnSteps)
            {
                if (stepsByName[dependencyStepName].FirstOrDefault() is { } dependencyStep)
                {
                    pendingSteps.Enqueue(dependencyStep);
                }
            }

            foreach (var requiredStep in requiredStepsByName[step.Name])
            {
                pendingSteps.Enqueue(requiredStep);
            }
        }
    }

    private static string GetUnsupportedPublishMessage(string resourceName) => GetUnsupportedPublishMessage([resourceName]);

    private static string GetUnsupportedPublishMessage(IReadOnlyList<string> resourceNames)
    {
        var subject = resourceNames.Count == 1
            ? $"Resource '{resourceNames[0]}' is a {nameof(DotnetProjectResource)}."
            : $"Resources {string.Join(", ", resourceNames.Select(name => $"'{name}'"))} are {nameof(DotnetProjectResource)} instances.";
        var publishTarget = resourceNames.Count == 1 ? "this resource" : "these resources";
        var runOnlySubject = resourceNames.Count == 1 ? "it" : "a resource";

        return $"{subject} Automatic project publishing for this experimental resource type is not supported. " +
            $"For a C# AppHost project reference, use {nameof(ProjectResourceBuilderExtensions.AddProject)}<TProject>(...). " +
            $"For a path-based project or file-based app, use {nameof(ProjectResourceBuilderExtensions.AddCSharpApp)}(...) in C# or addCSharpApp(...) in TypeScript. " +
            $"To publish {publishTarget} using explicit container configuration, call {nameof(ExecutableResourceBuilderExtensions.PublishAsDockerFile)}(...) in C# or publishAsDockerFile(...) in TypeScript. " +
            $"If {runOnlySubject} is intentionally run-only, call {nameof(ResourceBuilderExtensions.ExcludeFromManifest)}() in C# or excludeFromManifest() in TypeScript.";
    }
}
