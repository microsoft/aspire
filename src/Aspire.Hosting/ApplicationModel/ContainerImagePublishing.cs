// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES003
#pragma warning disable ASPIRECONTAINERRUNTIME001
#pragma warning disable ASPIRECOMPUTE003

using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Publishing;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Registers image source preparation and verified, registry-scoped image publication.
/// </summary>
internal static class ContainerImagePublishing
{
    internal static void Configure(IDistributedApplicationBuilder builder, ContainerImageResource source)
    {
        if (!builder.Services.Any(descriptor => descriptor.ServiceType == typeof(ConsumerConfiguration)))
        {
            var configuration = new ConsumerConfiguration();
            builder.Services.AddSingleton(configuration);
            builder.Pipeline.AddPipelineConfiguration(configuration.ConfigureAsync);
        }

        source.Annotations.Add(new PipelineStepAnnotation(factory =>
        {
            if (factory.PipelineContext.ExecutionContext.IsRunMode || source.IsExcludedFromPublish())
            {
                return [];
            }

            var destinations = source.GetPublications()
                .Select(publication => publication.Destination)
                .Where(destination => !destination.IsExcludedFromPublish())
                .ToArray();
            if (destinations.Length == 0 && !source.TryGetLastAnnotation<DockerfileBuildAnnotation>(out _))
            {
                return [];
            }
            var configuration = source.GetSource();
            var buildSource = configuration.Dockerfile is not null;
            if (destinations.Length == 0 && !buildSource)
            {
                return [];
            }

            foreach (var destination in destinations)
            {
                if (!factory.PipelineContext.Model.Resources.Contains(destination) ||
                    !factory.PipelineContext.Model.Resources.Any(resource =>
                        StringComparers.ResourceName.Equals(resource.Name, destination.Parent.Name) &&
                        resource is IContainerRegistry && !resource.IsExcludedFromPublish()))
                {
                    throw new DistributedApplicationException(
                        $"Destination image '{destination.Name}' and its registry '{destination.Parent.Name}' must be present in the publishable application model.");
                }
            }

            // A new closure is created on each pipeline resolution. Never reuse prepared content
            // across deployments: a mutable tag may move, or a previous preparation may have failed.
            PreparedImage? prepared = null;
            var prepareName = $"prepare-image-{source.Name}";
            var steps = new List<PipelineStep>
            {
                new()
                {
                    Name = prepareName,
                    Description = $"Prepares the image source for {source.Name}.",
                    Resource = source,
                    DependsOnSteps = buildSource
                        ? [WellKnownPipelineSteps.BuildPrereq]
                        : [WellKnownPipelineSteps.DeployPrereq, WellKnownPipelineSteps.PushPrereq],
                    RequiredBySteps = buildSource ? [WellKnownPipelineSteps.Build] : [],
                    Action = async context =>
                    {
                        prepared = null;
                        foreach (var destination in destinations)
                        {
                            destination.ClearPublishedDigest();
                        }
                        EnsureConfiguration(source, configuration);
                        var task = await context.ReportingStep.CreateTaskAsync(
                            new MarkdownString($"Preparing image **{source.Name}**"),
                            context.CancellationToken).ConfigureAwait(false);
                        await using var taskLifetime = task.ConfigureAwait(false);
                        try
                        {
                            var runtime = await context.Services.GetRequiredService<IContainerRuntimeResolver>()
                                .ResolveAsync(context.CancellationToken).ConfigureAwait(false);
                            if (configuration.Dockerfile is { } dockerfile)
                            {
                                var digest = await ContainerImageArtifactBuilder.BuildAsync(source, dockerfile, runtime, context)
                                    .ConfigureAwait(false);
                                context.CancellationToken.ThrowIfCancellationRequested();
                                EnsureConfiguration(source, configuration);
                                prepared = new(runtime, digest, digest, PublicationTag: null, LocalArtifact: true);
                                await task.CompleteAsync(new MarkdownString($"Built **{source.Name}** at `{digest}`"),
                                    CompletionState.Completed, context.CancellationToken).ConfigureAwait(false);
                                return;
                            }
                            var pushOptions = await source.ProcessImagePushOptionsCallbackAsync(context.CancellationToken).ConfigureAwait(false);
                            var publicationTag = pushOptions.RemoteImageTag;
                            if (string.IsNullOrWhiteSpace(publicationTag))
                            {
                                throw new DistributedApplicationException($"The publication tag for image source '{source.Name}' must not be empty.");
                            }

                            var reference = await runtime.ResolveRemoteImageAsync(configuration.Image, context.CancellationToken).ConfigureAwait(false);
                            var resolved = ContainerImageName.ParseSource(reference);
                            if (resolved.Digest is null ||
                                !StringComparer.Ordinal.Equals(reference, $"{configuration.Source.Registry}/{configuration.Source.Image}@{resolved.Digest}") ||
                                (configuration.Source.Digest is { } expected && !StringComparer.Ordinal.Equals(expected, resolved.Digest)))
                            {
                                throw new DistributedApplicationException($"Container runtime returned an invalid immutable source reference for image '{source.Name}'.");
                            }
                            context.CancellationToken.ThrowIfCancellationRequested();
                            EnsureConfiguration(source, configuration);
                            // Deploy prerequisites configure the same default label on compute and
                            // artifact sources. Consumers still use the verified content digest.
                            prepared = new(runtime, reference, resolved.Digest, publicationTag, LocalArtifact: false);
                            await task.CompleteAsync(new MarkdownString($"Prepared **{source.Name}** at `{reference}`"),
                                CompletionState.Completed, context.CancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            await task.FailAsync(new MarkdownString($"Failed to prepare **{source.Name}**: {ex.Message}"),
                                CancellationToken.None).ConfigureAwait(false);
                            throw;
                        }
                    }
                }
            };
            foreach (var destination in destinations)
            {
                steps.Add(new PipelineStep
                {
                    Name = $"push-{destination.Name}",
                    Description = $"Publishes {source.Name} to the {destination.Parent.Name} registry as {destination.Name}.",
                    Resource = destination,
                    Tags = [WellKnownPipelineTags.PushContainerImage],
                    DependsOnSteps = buildSource
                        ? [prepareName, WellKnownPipelineSteps.DeployPrereq, WellKnownPipelineSteps.PushPrereq]
                        : [prepareName],
                    // Standalone images must be reachable even without a compute deployment target.
                    RequiredBySteps = [WellKnownPipelineSteps.Push, WellKnownPipelineSteps.Deploy],
                    Action = context => PublishAsync(destination, configuration,
                        prepared ?? throw new DistributedApplicationException($"Image '{source.Name}' has not been prepared."),
                        context)
                });
            }

            return steps;
        }));

        source.Annotations.Add(new PipelineConfigurationAnnotation(context =>
        {
            foreach (var destination in source.GetPublications().Select(publication => publication.Destination))
            {
                context.GetSteps(destination, WellKnownPipelineTags.PushContainerImage)
                    .DependsOn(context.GetSteps(destination.Parent, WellKnownPipelineTags.ProvisionInfrastructure));
            }
        }));
    }

    private static async Task PublishAsync(
        DestinationImageResource destination,
        ContainerImageSourceAnnotation configuration,
        PreparedImage prepared,
        PipelineStepContext context)
    {
        EnsureConfiguration(destination.Source, configuration);
        destination.EnsureCurrentPublication();
        var task = await context.ReportingStep.CreateTaskAsync(
            new MarkdownString($"Pushing image **{destination.Name}** to **{destination.Parent.Name}**"),
            context.CancellationToken).ConfigureAwait(false);
        await using var taskLifetime = task.ConfigureAwait(false);
        try
        {
            var repository = await destination.GetRepositoryAsync(
                new ValueProviderContext { ExecutionContext = context.ExecutionContext }, context.CancellationToken).ConfigureAwait(false);
            // Build-only execution does not initialize deploy labels or log into registries.
            // Read the artifact publication label only after those push prerequisites complete.
            var publicationTag = prepared.PublicationTag ??
                (await destination.Source.ProcessImagePushOptionsCallbackAsync(context.CancellationToken).ConfigureAwait(false)).RemoteImageTag;
            if (string.IsNullOrWhiteSpace(publicationTag))
            {
                throw new DistributedApplicationException($"The publication tag for image source '{destination.Source.Name}' must not be empty.");
            }
            var taggedDestination = $"{repository}:{publicationTag}";
            string reference;
            if (prepared.LocalArtifact)
            {
                if (prepared.Runtime is not IContainerImageArtifactRuntime artifactRuntime)
                {
                    throw new DistributedApplicationException($"Container runtime '{prepared.Runtime.Name}' cannot publish the prepared image artifact.");
                }
                reference = await artifactRuntime.PublishImageArtifactAsync(
                    prepared.Digest, taggedDestination, context.CancellationToken).ConfigureAwait(false);
            }
            else
            {
                reference = await prepared.Runtime.CopyRemoteImageAsync(
                    prepared.Reference, taggedDestination, context.CancellationToken).ConfigureAwait(false);
            }
            if (!StringComparer.Ordinal.Equals(reference, $"{repository}@{prepared.Digest}"))
            {
                throw new DistributedApplicationException($"Container runtime did not verify the expected content reference for destination image '{destination.Name}'.");
            }
            context.CancellationToken.ThrowIfCancellationRequested();
            EnsureConfiguration(destination.Source, configuration);
            destination.EnsureCurrentPublication();

            // Each destination owns a section so parallel fan-out cannot race on one JSON object.
            // Persist last successful evidence, but never hydrate it as proof of a new publication.
            var state = context.Services.GetRequiredService<IDeploymentStateManager>();
            var section = await state.AcquireSectionAsync($"ContainerImages:{destination.Name}", context.CancellationToken).ConfigureAwait(false);
            section.Data.Clear();
            section.Data["schemaVersion"] = 1;
            section.Data["sourceImage"] = prepared.Reference;
            section.Data["destinationImage"] = reference;
            section.Data["publicationTag"] = taggedDestination;
            await state.SaveSectionAsync(section, context.CancellationToken).ConfigureAwait(false);
            context.CancellationToken.ThrowIfCancellationRequested();
            EnsureConfiguration(destination.Source, configuration);
            destination.RecordPublishedImage(repository, prepared.Digest, publicationTag);
            context.Summary.Add($"Image {destination.Name}", reference);
            await task.CompleteAsync(new MarkdownString($"Published **{destination.Name}** as `{reference}`"),
                CompletionState.Completed, context.CancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            destination.ClearPublishedDigest();
            await task.FailAsync(new MarkdownString($"Failed to push **{destination.Name}**: {ex.Message}"),
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static void EnsureConfiguration(ContainerImageResource source, ContainerImageSourceAnnotation configuration)
    {
        if (!ReferenceEquals(source.GetSource(), configuration))
        {
            throw new DistributedApplicationException(
                $"Image source '{source.Name}' changed after the publication pipeline was resolved. Retry the deployment.");
        }
    }

    private sealed class ConsumerConfiguration
    {
        private readonly ResourceDependencyDiscoveryOptions _dependencyOptions = new()
        {
            DiscoveryMode = ResourceDependencyDiscoveryMode.DirectOnly
        };

        public async Task ConfigureAsync(PipelineConfigurationContext context)
        {
            var executionContext = context.Services.GetRequiredService<DistributedApplicationExecutionContext>();
            if (executionContext.IsRunMode)
            {
                return;
            }

            foreach (var consumer in context.Model.GetComputeResources().Where(resource => !resource.IsExcludedFromPublish()))
            {
                var dependencies = await consumer.GetResourceDependenciesAsync(
                    executionContext, _dependencyOptions).ConfigureAwait(false);
                foreach (var image in dependencies.OfType<DestinationImageResource>())
                {
                    var pushSteps = context.GetSteps(image, WellKnownPipelineTags.PushContainerImage).ToArray();
                    if (pushSteps.Length == 0 || !context.Model.Resources.Contains(image) ||
                        !context.Model.Resources.Contains(image.Source))
                    {
                        throw new DistributedApplicationException(
                            $"Resource '{consumer.Name}' consumes destination image '{image.Name}', but that image has no publishable source and destination in the application model.");
                    }

                    context.GetSteps(consumer, WellKnownPipelineTags.DeployCompute).DependsOn(pushSteps);
                    foreach (var target in consumer.Annotations.OfType<DeploymentTargetAnnotation>())
                    {
                        // Azure applies workload settings during provisioning. Compose and Helm
                        // resolve deferred values during deployment preparation, before applying them.
                        context.GetSteps(target.DeploymentTarget, WellKnownPipelineTags.ProvisionInfrastructure).DependsOn(pushSteps);
                        context.GetSteps(target.DeploymentTarget, WellKnownPipelineTags.DeployCompute).DependsOn(pushSteps);
                        if (target.ComputeEnvironment is { } environment)
                        {
                            context.GetSteps(environment, WellKnownPipelineTags.DeployCompute).DependsOn(pushSteps);
                        }
                    }
                }
            }
        }
    }

    private sealed record PreparedImage(
        IContainerRuntime Runtime, string Reference, string Digest, string? PublicationTag, bool LocalArtifact);
}
