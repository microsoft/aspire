// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Hosting.Native.Client;

namespace Aspire.Hosting.Native.Authoring;

/// <summary>Native-exported desired state and explicit deployment coordinates. No runtime values are resolved here.</summary>
/// <param name="Model">The sealed, secret-free native publishing projection.</param>
/// <param name="OutputPath">Absolute artifact directory.</param>
/// <param name="ApplicationDirectory">Absolute application source directory.</param>
/// <param name="EnvironmentName">Deployment environment identity.</param>
/// <param name="CancellationToken">Cancels callbacks and tools.</param>
public sealed record PublishingContext(
    JsonObject Model, string OutputPath, string ApplicationDirectory, string EnvironmentName, CancellationToken CancellationToken)
{
    /// <summary>Gets whether this context executes startup rather than publication callbacks.</summary>
    public bool IsRunMode { get; init; }
}

/// <summary>An inert compute environment, independent of runtime readiness dependencies.</summary>
/// <param name="name">Native resource identity.</param>
/// <param name="typeId">Integration resource type identity.</param>
public class ComputeEnvironmentResource(string name, string typeId) : Resource(name)
{
    /// <summary>Gets the integration's native resource type identity.</summary>
    public string TypeId { get; } = typeId;
}

public sealed partial class DistributedApplicationBuilder
{
    /// <summary>Gets whether authoring records deployment rather than local execution intent.</summary>
    public bool IsPublishMode => Configuration["operation"] is "publish" or "inspect";
    /// <summary>Gets the command recorder for native-scheduled callbacks.</summary>
    public NativePipeline Pipeline { get; } = new();
    /// <summary>Gets the absolute application source directory used to resolve authored paths.</summary>
    public string AppHostDirectory => Settings.ApplicationDirectory;

    /// <summary>Declares a native compute environment in publish mode, or returns an inert builder in run mode.</summary>
    /// <typeparam name="T">Integration-owned environment type.</typeparam>
    /// <param name="resource">Inert environment data.</param>
    /// <returns>The typed environment builder.</returns>
    public IResourceBuilder<T> AddComputeEnvironment<T>(T resource) where T : ComputeEnvironmentResource
    {
        if (IsPublishMode)
        {
            Add(resource);
            _declarations.Add(async context => context.Bind(resource,
                await context.Composition.AddComputeEnvironmentAsync(resource.Name, resource.TypeId, context.Token).ConfigureAwait(false)));
        }

        return new ResourceBuilder<T>(this, resource);
    }

    /// <summary>Records a configuration-backed parameter, leaving it unbound during publishing.</summary>
    /// <param name="name">Parameter identity.</param>
    /// <param name="secret">Whether native storage treats this input as secret.</param>
    /// <returns>A symbolic parameter builder.</returns>
    public IResourceBuilder<ParameterResource> AddConfiguredParameter(string name, bool secret)
    {
        var resource = new ParameterResource(name, secret);
        Add(resource);
        _declarations.Insert(0, async context => context.BindParameter(resource,
            await context.Composition.AddConfiguredParameterAsync(name, secret, IsPublishMode, context.Token).ConfigureAwait(false)));

        return new ResourceBuilder<ParameterResource>(this, resource);
    }

    /// <summary>Materializes and publishes through an already authenticated native client. Does not start workloads.</summary>
    /// <param name="client">Caller-owned authenticated transport.</param>
    /// <param name="outputPath">Absolute or application-relative artifact path.</param>
    /// <param name="environmentName">Deployment environment identity.</param>
    /// <param name="target">Pipeline target and transitive prerequisites to execute.</param>
    /// <param name="cancellationToken">Cancels materialization, callbacks and tools.</param>
    /// <returns>The native-exported publishing context after successful execution.</returns>
    /// <exception cref="InvalidOperationException">The builder is not in publish mode.</exception>
    public async Task<PublishingContext> PublishAsync(NativeClient client, string outputPath, string environmentName,
        string target, CancellationToken cancellationToken)
    {
        if (!IsPublishMode)
        {
            throw new InvalidOperationException("Publishing requires --operation=publish when creating the builder.");
        }
        var session = await client.Server.OpenApplicationAsync(cancellationToken).ConfigureAwait(false);
        var composition = await session.StartGenerationAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await MaterializeAsync(composition, cancellationToken).ConfigureAwait(false);
            var model = JsonNode.Parse(await composition.ExportPublishModelAsync(cancellationToken).ConfigureAwait(false))!.AsObject();
            var context = new PublishingContext(model, Path.GetFullPath(outputPath, AppHostDirectory),
                AppHostDirectory, environmentName, cancellationToken);
            await Pipeline.ExecuteAsync(client.Server, context, target).ConfigureAwait(false);

            return context;
        }
        finally
        {
            await session.RetireGenerationAsync(composition, CancellationToken.None).ConfigureAwait(false);
        }
    }
}

/// <summary>Records generic publishing metadata and native compute relationships.</summary>
public static class PublishingResourceBuilderExtensions
{
    /// <summary>Records a native compute assignment without introducing a readiness edge.</summary>
    /// <typeparam name="T">Workload resource type.</typeparam>
    /// <param name="builder">Assigned workload.</param>
    /// <param name="environment">Environment in the same authoring plan.</param>
    /// <returns>The workload builder.</returns>
    /// <exception cref="ArgumentException">The environment belongs to another plan.</exception>
    public static IResourceBuilder<T> WithComputeEnvironment<T>(this IResourceBuilder<T> builder,
        IResourceBuilder<ComputeEnvironmentResource> environment) where T : Resource
    {
        if (!ReferenceEquals(builder.ApplicationBuilder, environment.ApplicationBuilder))
        {
            throw new ArgumentException("The compute environment belongs to another builder.", nameof(environment));
        }
        if (builder.ApplicationBuilder.IsPublishMode)
        {
            builder.ApplicationBuilder.Record(context => context.Declaration(builder.Resource)
                .WithComputeEnvironmentAsync(context.Declaration(environment.Resource), context.Token));
        }

        return builder;
    }

    /// <summary>Records integration metadata evaluated at materialization, preserving other configuration.</summary>
    /// <typeparam name="T">Resource type.</typeparam>
    /// <param name="builder">Metadata owner.</param>
    /// <param name="name">Integration metadata key without the publish prefix.</param>
    /// <param name="value">Returns the final serialized authoring metadata.</param>
    /// <returns>The resource builder.</returns>
    public static IResourceBuilder<T> WithPublishConfiguration<T>(this IResourceBuilder<T> builder,
        string name, Func<string> value) where T : Resource
    {
        if (builder.ApplicationBuilder.IsPublishMode)
        {
            builder.ApplicationBuilder.Record(async context =>
            {
                var declaration = context.Declaration(builder.Resource);
                var current = await declaration.InspectResourceAsync(context.Token).ConfigureAwait(false);
                await declaration.SetResourceConfigurationAsync(new()
                {
                    Properties = [.. current.Configuration.Where(property => property.Name != "publish." + name),
                        new() { Name = "publish." + name, Value = value() }]
                }, context.Token).ConfigureAwait(false);
            });
        }

        return builder;
    }

    /// <summary>Marks HTTP endpoints as externally exposed deployment intent.</summary>
    /// <typeparam name="T">Resource type.</typeparam>
    /// <param name="builder">Endpoint owner.</param>
    /// <returns>The resource builder.</returns>
    public static IResourceBuilder<T> WithExternalHttpEndpoints<T>(this IResourceBuilder<T> builder) where T : Resource
    {
        builder.ApplicationBuilder.Record(async context =>
            await context.Declaration(builder.Resource).WithExternalHttpEndpointsAsync(context.Token).ConfigureAwait(false));

        return builder;
    }

    /// <summary>Records a bind mount without requiring its source to exist on the authoring machine.</summary>
    /// <typeparam name="T">Resource type.</typeparam>
    /// <param name="builder">Mount recipient.</param>
    /// <param name="source">Host source path.</param>
    /// <param name="target">Absolute container target.</param>
    /// <param name="readOnly">Whether the mount disallows writes.</param>
    /// <returns>The resource builder.</returns>
    public static IResourceBuilder<T> WithBindMount<T>(this IResourceBuilder<T> builder,
        string source, string target, bool readOnly = false) where T : Resource
    {
        builder.ApplicationBuilder.Record(async context =>
            await context.Declaration(builder.Resource).WithBindMountAsync(source, target, readOnly, context.Token).ConfigureAwait(false));

        return builder;
    }
}
