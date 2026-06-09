#pragma warning disable ASPIRECSHARPAPPS001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable ASPIRECERTIFICATES001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable ASPIRECONTAINERRUNTIME001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable ASPIREDOTNETTOOL // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using Aspire.Dashboard.Model;
using Aspire.Hosting.Publishing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

public static class Extensions
{
    public static IResourceBuilder<T> RunAsContainer<T>(this IResourceBuilder<T> builder)
        where T : ProjectResource
    {
        if (!builder.ApplicationBuilder.ExecutionContext.IsRunMode)
        {
            return builder;
        }

        var imagePublisher = AddContainerPublisher();

        TransmuteResourceAnnotations();
        FixEndpoints();

        return builder
            .WaitForCompletion(imagePublisher)
            .WithDotnetContainerDefaults()
            .WithInitialState(new CustomResourceSnapshot { ResourceType = KnownResourceTypes.Container, Properties = [] });

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

            var appHostName = builder.ApplicationBuilder.AppHostAssembly!.GetName().Name!.ToLowerInvariant();
            builder.WithAnnotation(new ContainerImageAnnotation
            {
                Image = $"aspire/{appHostName}/{builder.Resource.Name}",
                Tag = "aspire-image-build",
                Registry = "" // Use local registry
            }, ResourceAnnotationMutationBehavior.Replace);
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
        IResourceBuilder<ExecutableResource> AddContainerPublisher()
        {
            return builder.ApplicationBuilder.AddExecutable($"{builder.Resource.Name}-publisher", "dotnet", ".")
                .WithArgs("publish", builder.Resource.GetProjectMetadata().ProjectPath, "/t:PublishContainer")
                .WithIconName("BoxToolbox")
                .WithParentRelationship(builder)
                .WaitForContainerRuntime()
                .WithArgs(ctx =>
                {
                    // Lazy set the image metadata in case someone mutates the image annotation
                    if (builder.Resource.TryGetLastAnnotation<ContainerImageAnnotation>(out var imageAnnotation))
                    {
                        ctx.Args.Add($"/p:ContainerRepository=\"{imageAnnotation.Image}\"");
                        ctx.Args.Add($"/p:ContainerImageTags=\"{imageAnnotation.Tag}\"");
                        ctx.Args.Add($"/p:ContainerRegistry=\"{imageAnnotation.Registry}\"");
                    }
                });
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
        where T : ProjectResource
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
            var projectPath = builder.Resource.GetProjectMetadata().ProjectPath;
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
                    ctx.Args.Add(GetToolPackageOutputPath(ctx.ExecutionContext.ServiceProvider));
                })
                .WithIconName("BoxToolbox")
                .WithParentRelationship(builder);
        }

        static string GetToolPackageOutputPath(IServiceProvider services)
        {
            var aspireStore = services.GetRequiredService<IAspireStore>();

            var toolPackageOutputPath = Path.Combine(aspireStore.BasePath, "tools");
            Directory.CreateDirectory(toolPackageOutputPath);

            return toolPackageOutputPath;
        }
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