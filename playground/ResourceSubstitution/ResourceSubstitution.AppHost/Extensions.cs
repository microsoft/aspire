#pragma warning disable ASPIRECSHARPAPPS001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable ASPIRECERTIFICATES001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable ASPIRECONTAINERRUNTIME001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable ASPIREDOTNETTOOL // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using Aspire.Hosting.Publishing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

public static class Extensions
{
    public static IResourceBuilder<T> RunAsContainer<T>(this IResourceBuilder<T> builder)
        where T : IResourceWithEnvironment, IResourceWithArgs, IResourceWithEndpoints, IResourceWithWaitSupport
    {
        if (!builder.ApplicationBuilder.ExecutionContext.IsRunMode)
        {
            return builder;
        }

        var projectPath = builder.GetProjectPath();

        // Every resource built from the same project produces the exact same image, so publishing it once and
        // sharing that image (and the publisher that builds it) across all of them is both correct and avoids
        // running redundant, concurrent `dotnet publish` invocations of the same project.
        var image = GetContainerImage(projectPath);
        var imagePublisher = GetOrAddContainerPublisher(projectPath, image);

        TransmuteResourceAnnotations();
        FixEndpoints();

        return builder
            .WaitForCompletion(imagePublisher)
            .WithDotnetContainerDefaults();

        ContainerImageAnnotation GetContainerImage(string projectPath)
        {
            var appHostName = builder.ApplicationBuilder.AppHostAssembly!.GetName().Name!.ToLowerInvariant();
            return new ContainerImageAnnotation
            {
                Image = $"aspire/{appHostName}/{GetSanitizedProjectName(projectPath)}",
                Tag = "aspire-image-build",
                Registry = "" // Use local registry
            };
        }

        void TransmuteResourceAnnotations()
        {
            if (!builder.Resource.TryGetLastAnnotation<IProjectMetadata>(out var projectMetadata))
            {
                throw new InvalidOperationException("RunAsContainer can only be used on resources with project metadata.");
            }
            builder.Resource.Annotations.Remove(projectMetadata);

            if (builder.Resource.TryGetLastAnnotation<ExecutableAnnotation>(out var executableAnnotation))
            {
                builder.Resource.Annotations.Remove(executableAnnotation);
            }

            builder.ApplicationBuilder.RemoveRebuilderResource(builder.Resource.Name);

            builder.WithAnnotation(image, ResourceAnnotationMutationBehavior.Replace);
        }

        // As a project, the target port is left null.
        // For executables, DCP will allocate it's own port if the target port is null
        // This does not happen for containers, so we give those endpoints explicit ports
        void FixEndpoints()
        {
            //TODO: logic isn't complete - this doesn't currently consider Kestrel endpoint configuration.
            var http = builder.GetEndpoint("http");
            if (http.Exists && http.EndpointAnnotation.TargetPort is null)
            {
                http.EndpointAnnotation.TargetPort = 8000;
                builder.WithEnvironment("ASPNETCORE_HTTP_PORTS", http.Property(EndpointProperty.TargetPort));
            }

            var https = builder.GetEndpoint("https");
            if (https.Exists && https.EndpointAnnotation.TargetPort is null)
            {
                https.EndpointAnnotation.TargetPort = 8443;
                builder.WithEnvironment("ASPNETCORE_HTTPS_PORTS", https.Property(EndpointProperty.TargetPort));
            }

            // `ASPNETCORE_URLS` typically has `localhost` as the host
            // But for containers, we need to bind to all interfaces so the tunnel can access it
            builder.WithEnvironment(ctx => ctx.EnvironmentVariables.Remove("ASPNETCORE_URLS"));
        }

        // This could potentially use `IResourceContainerImageManager` instead, but this mirrors
        // the tool publishing approach, and is easier to troubleshoot errors in run mode.
        //
        // Idempotent per project path: every resource built from the same project shares this one publisher
        // instead of each getting its own, so the project is only ever published once, not once per consumer.
        IResourceBuilder<ExecutableResource> GetOrAddContainerPublisher(string projectPath, ContainerImageAnnotation image)
        {
            var publisherName = $"{Path.GetFileNameWithoutExtension(projectPath)}-publisher";
            if (builder.ApplicationBuilder.TryCreateResourceBuilder<ExecutableResource>(publisherName, out var existing))
            {
                return existing;
            }

            // Built from the project path rather than a user-provided resource name, so it can contain
            // characters (e.g. the project file's '.') that the default resource-name validation rejects.
            var publisher = new ExecutableResource(publisherName, "dotnet", builder.ApplicationBuilder.AppHostDirectory);
            publisher.Annotations.Add(NameValidationPolicyAnnotation.None);

            return builder.ApplicationBuilder.AddResource(publisher)
                .WithArgs(
                    "publish", projectPath, "/t:PublishContainer",
                    $"/p:ContainerRepository=\"{image.Image}\"",
                    $"/p:ContainerImageTags=\"{image.Tag}\"",
                    $"/p:ContainerRegistry=\"{image.Registry}\"")
                .WithIconName("BoxToolbox")
                .WaitForContainerRuntime()
                .ExcludeFromManifest();
        }
    }

    private static string GetSanitizedProjectName(string projectPath) =>
        Path.GetFileNameWithoutExtension(projectPath).Replace('.', '-').ToLowerInvariant();

    // AddProject/AddCSharpApp/AddDotnetProject all add a hidden "{name}-rebuilder" companion resource (via
    // WithProjectDefaults) as a side effect. Once RunAsContainer/RunAsProject/RunAsTool convert a resource away
    // from being a project, that companion is left behind referencing a resource that's no longer a project —
    // remove it so it doesn't leak into the app model.
    private static void RemoveRebuilderResource(this IDistributedApplicationBuilder appBuilder, string resourceName)
    {
        var rebuilder = appBuilder.Resources.FirstOrDefault(r => r.Name == $"{resourceName}-rebuilder");
        if (rebuilder is not null)
        {
            appBuilder.Resources.Remove(rebuilder);
        }
    }

    public static IResourceBuilder<T> RunAsProject<T>(this IResourceBuilder<T> builder, string projectPath)
        where T : ContainerResource
    {
        if (!builder.ApplicationBuilder.ExecutionContext.IsRunMode)
        {
            return builder;
        }

        TransmuteAnnotations();
        FixEndpoints();
        return builder;

        void TransmuteAnnotations()
        {
            if (builder.Resource.TryGetLastAnnotation<ContainerImageAnnotation>(out var containerAnnotation))
            {
                builder.Resource.Annotations.Remove(containerAnnotation);
            }
            if (builder.Resource.TryGetLastAnnotation<ExecutableAnnotation>(out var executableAnnotation))
            {
                builder.Resource.Annotations.Remove(executableAnnotation);
            }
            if (builder.Resource.TryGetLastAnnotation<DotnetToolAnnotation>(out var dotnetToolAnnotation))
            {
                builder.Resource.Annotations.Remove(dotnetToolAnnotation);
            }

            // For now, create a dummy csharp app resource, then copy it's annotations to our new resource
            //
            // Exposing ProjectResourceBuilderExtensions.WithProjectDefaults may be a cleaner approach in the long run
            // And making it usable on any `IResource`
            var newProject = builder.ApplicationBuilder.AddCSharpApp($"temp-{Guid.NewGuid()}", projectPath);
            builder.ApplicationBuilder.Resources.Remove(newProject.Resource);

            // Removing the temp project above doesn't remove its "-rebuilder" companion (see RemoveRebuilderResource).
            builder.ApplicationBuilder.RemoveRebuilderResource(newProject.Resource.Name);

            // TODO: A clever merge approach may be needed here
            foreach (var annotation in newProject.Resource.Annotations)
            {
                builder.Resource.Annotations.Add(annotation);
            }
        }

        void FixEndpoints()
        {
            // The endpoint references on the temp project resource have a reference back to the temp resource
            // Which will never become available.
            // If using `WithProjectDefaults`, this should no longer not be necessary
            builder.WithEnvironment(ctx =>
            {
                ctx.EnvironmentVariables.Remove("ASPNETCORE_URLS");

                foreach (var endpointName in new[] { "http", "https" })
                {
                    var endpoint = builder.GetEndpoint(endpointName);
                    if (endpoint.Exists)
                    {
                        ctx.EnvironmentVariables[$"ASPNETCORE_{endpointName.ToUpperInvariant()}_PORTS"] = endpoint.Property(EndpointProperty.TargetPort);
                    }
                }
            });
        }
    }

    // This is a slightly silly example - not sure why you'd ever want to run a project as a tool
    // But it helps to prove the model out.
    public static IResourceBuilder<T> RunAsTool<T>(this IResourceBuilder<T> builder)
        where T : IResourceWithEnvironment, IResourceWithArgs, IResourceWithEndpoints, IResourceWithWaitSupport
    {
        if (!builder.ApplicationBuilder.ExecutionContext.IsRunMode)
        {
            return builder;
        }

        var toolPublisher = AddToolPublisher();

        TransmuteResource();

        return builder
            .WaitForCompletion(toolPublisher);

        void TransmuteResource()
        {
            if (builder.Resource.TryGetLastAnnotation<IProjectMetadata>(out var projectMetadataAnnotation))
            {
                builder.Resource.Annotations.Remove(projectMetadataAnnotation);
            }
            if (builder.Resource.TryGetLastAnnotation<ContainerImageAnnotation>(out var containerImageAnnotation))
            {
                builder.Resource.Annotations.Remove(containerImageAnnotation);
            }

            builder.ApplicationBuilder.RemoveRebuilderResource(builder.Resource.Name);

            // again, rather than copy
            var newTool = builder.ApplicationBuilder.AddDotnetTool($"temp-{Guid.NewGuid()}", builder.Resource.Name)
                .WithToolIgnoreExistingFeeds()
                .WithToolPrerelease();

            builder.ApplicationBuilder.Resources.Remove(newTool.Resource);

            foreach (var annotation in newTool.Resource.Annotations)
            {
                builder.Resource.Annotations.Add(annotation);
            }

            builder.OnBeforeResourceStarted((resource, evt, ct) =>
            {
                var outputPath = GetToolPackageOutputPath(evt.Services);
                newTool.WithToolSource(outputPath);
                return Task.CompletedTask;
            });
        }

        IResourceBuilder<ExecutableResource> AddToolPublisher()
        {
            var projectPath = builder.GetProjectPath();
            return builder.ApplicationBuilder.AddExecutable($"{builder.Resource.Name}-tool-publisher", "dotnet", ".")
                .WithArgs(ctx =>
                {
                    ctx.Args.Add("pack");
                    ctx.Args.Add(projectPath);
                    ctx.Args.Add("--no-build");
                    ctx.Args.Add("-p:IsPackable=true");
                    ctx.Args.Add("-p:PackAsTool=true");
                    ctx.Args.Add($"-p:PackageId=\"{builder.Resource.Name}\"");
                    ctx.Args.Add("--output");
                    ctx.Args.Add(GetToolPackageOutputPath(ctx.ExecutionContext.Services));
                })
                .WithIconName("BoxToolbox")
                .WithParentRelationship(builder.Resource);
        }

        static string GetToolPackageOutputPath(IServiceProvider services)
        {
            var aspireStore = services.GetRequiredService<IAspireStore>();

            var toolPackageOutputPath = Path.Combine(aspireStore.BasePath, "tools");
            Directory.CreateDirectory(toolPackageOutputPath);

            return toolPackageOutputPath;
        }
    }

    // Annotation-based rather than the ProjectResource-typed GetProjectMetadata() extension, so RunAsContainer/
    // RunAsTool work on any resource carrying project metadata (e.g. DotnetProjectResource), not just ProjectResource.
    private static string GetProjectPath<T>(this IResourceBuilder<T> builder)
        where T : IResource
    {
        if (!builder.Resource.TryGetLastAnnotation<IProjectMetadata>(out var projectMetadata))
        {
            throw new InvalidOperationException($"Resource '{builder.Resource.Name}' is missing required project metadata.");
        }

        return projectMetadata.ProjectPath;
    }

    private static IResourceBuilder<T> WithDotnetContainerDefaults<T>(this IResourceBuilder<T> builder)
        where T : IResourceWithEnvironment, IResourceWithArgs
    {
        return builder
            .WithDeveloperCertificateTrust(true)
            .WithHttpsDeveloperCertificate()
            .WithHttpsCertificateConfiguration(ctx =>
            {
                ctx.EnvironmentVariables["Kestrel__Certificates__Default__Path"] = ctx.CertificatePath;
                ctx.EnvironmentVariables["Kestrel__Certificates__Default__KeyPath"] = ctx.KeyPath;
                if (ctx.Password is not null)
                {
                    ctx.EnvironmentVariables["Kestrel__Certificates__Default__Password"] = ctx.Password;
                }

                return Task.CompletedTask;
            });
    }

    private static IResourceBuilder<T> WaitForContainerRuntime<T>(this IResourceBuilder<T> builder)
        where T : IResource
    {
        return builder.OnBeforeResourceStarted(async (resource, evt, ct) =>
        {
            var runtimeResolver = evt.Services.GetRequiredService<IContainerRuntimeResolver>();

            var runtime = await runtimeResolver.ResolveAsync(ct);

            var isRunning = await runtime.CheckIfRunningAsync(ct);

            if (isRunning)
            {
                return;
            }

            ResourceStateSnapshot? beforeWaitState = null;
            var rns = evt.Services.GetRequiredService<ResourceNotificationService>();
            await rns.PublishUpdateAsync(resource, x =>
            {
                beforeWaitState = x.State;
                return x with { State = KnownResourceStates.RuntimeUnhealthy };
            });

            var logger = evt.Services.GetRequiredService<ResourceLoggerService>().GetLogger(resource);
            logger.LogInformation("Waiting for container runtime {RuntimeName} to be available...", runtime.Name);

            while (!isRunning)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
                isRunning = await runtime.CheckIfRunningAsync(ct);
            }

            await rns.PublishUpdateAsync(resource, x => x with { State = beforeWaitState });
        });
    }
}