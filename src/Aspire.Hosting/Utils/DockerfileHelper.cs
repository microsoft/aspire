// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREDOCKERFILEBUILDER001
#pragma warning disable ASPIREPIPELINES003

using System.Text;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ApplicationModel.Docker;
using Aspire.Hosting.Publishing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Utils;

internal static class DockerfileHelper
{
    internal static DockerfileBuildAnnotation CloneBuildAnnotation(DockerfileBuildAnnotation original)
    {
        var annotation = new DockerfileBuildAnnotation(original.ContextPath, original.DockerfilePath, original.Stage)
        {
            DockerfileFactory = original.DockerfileFactory,
            ImageName = original.ImageName,
            ImageTag = original.ImageTag,
            HasEntrypoint = original.HasEntrypoint,
            BuildContextIgnoreContent = original.BuildContextIgnoreContent
        };
        foreach (var argument in original.BuildArguments)
        {
            annotation.BuildArguments.Add(argument.Key, argument.Value);
        }
        foreach (var secret in original.BuildSecrets)
        {
            annotation.BuildSecrets.Add(secret.Key, secret.Value);
        }

        return annotation;
    }

    internal static ContainerBuildOptionsCallbackAnnotation CreateDefaultBuildOptions() => new(context =>
    {
        if (context.Resource.TryGetLastAnnotation<DockerfileBuildAnnotation>(out var annotation))
        {
            context.LocalImageName = annotation.ImageName ?? context.Resource.Name;
            context.LocalImageTag = annotation.ImageTag ?? "latest";
        }
        else
        {
            context.LocalImageName = context.Resource.Name;
            context.LocalImageTag = "latest";
        }

        // Publish outputs must not depend on the developer's architecture. Run mode
        // leaves the platform unset so Docker and Podman use their native platform.
        if (context.ExecutionContext.IsPublishMode)
        {
            context.TargetPlatform = ContainerTargetPlatform.LinuxAmd64;
        }
    });

    internal static async Task<string> CreateDockerfileFromBuilderAsync(DockerfileFactoryContext context)
    {
        var dockerfileBuilder = new DockerfileBuilder();
        var callbackContext = new DockerfileBuilderCallbackContext(
            context.Resource, dockerfileBuilder, context.Services, context.CancellationToken);

        if (context.Resource.TryGetLastAnnotation<DockerfileBuilderCallbackAnnotation>(out var annotation))
        {
            foreach (var callback in annotation.Callbacks)
            {
                await callback(callbackContext).ConfigureAwait(false);
            }
        }

        // Dockerfiles use LF and UTF-8 without a BOM, regardless of the AppHost's OS.
        using var stream = new MemoryStream();
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true);
        writer.NewLine = "\n";
        await dockerfileBuilder.WriteAsync(writer, context.CancellationToken).ConfigureAwait(false);
        await writer.FlushAsync(context.CancellationToken).ConfigureAwait(false);
        stream.Position = 0;
        using var reader = new StreamReader(stream);

        return await reader.ReadToEndAsync(context.CancellationToken).ConfigureAwait(false);
    }

    internal static DockerfileBuildAnnotation GetBuildAnnotation(IResource resource, string methodName) =>
        resource.Annotations.OfType<DockerfileBuildAnnotation>().SingleOrDefault()
        ?? throw new InvalidOperationException(
            $"The resource '{resource.Name}' does not have a Dockerfile build annotation. Call WithDockerfile before calling {methodName}.");

    /// <summary>
    /// Executes the dockerfile factory if present and writes the generated content to the specified path.
    /// </summary>
    /// <param name="annotation">The dockerfile build annotation containing the factory.</param>
    /// <param name="resource">The resource for which the dockerfile is being generated.</param>
    /// <param name="serviceProvider">The service provider to be passed to the factory context.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public static async Task ExecuteDockerfileFactoryAsync(
        DockerfileBuildAnnotation annotation,
        IResource resource,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken)
    {
        if (annotation.DockerfileFactory is not null)
        {
            var context = new DockerfileFactoryContext
            {
                Services = serviceProvider,
                Resource = resource,
                CancellationToken = cancellationToken
            };

            await annotation.EmitDockerfileArtifactsAsync(context).ConfigureAwait(false);

            var executionContext = serviceProvider.GetRequiredService<DistributedApplicationExecutionContext>();

            if (executionContext.IsRunMode)
            {
                var rls = serviceProvider.GetRequiredService<ResourceLoggerService>();
                var logger = rls.GetLogger(resource);

                // Read the materialized Dockerfile content for logging
                var dockerfileContent = await File.ReadAllTextAsync(annotation.DockerfilePath, cancellationToken).ConfigureAwait(false);
                logger.LogInformation(
                    "Wrote generated Dockerfile at {DockerfilePath} using factory for resource {ResourceName}:\n{DockerfileContent}",
                    annotation.DockerfilePath,
                    resource.Name,
                    dockerfileContent);
            }
        }
    }
}
