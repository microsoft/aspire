// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Orleans;

namespace Aspire.Hosting;

/// <summary>
/// Extensions to <see cref="IDistributedApplicationBuilder"/> related to Orleans.
/// </summary>
public static class OrleansServiceExtensions
{
    private static readonly ProviderConfiguration s_inMemoryReminderService = new("Memory");
    private static readonly ProviderConfiguration s_inMemoryGrainStorage = new("Memory");
    private static readonly ProviderConfiguration s_inMemoryStreaming = new("Memory");
    private static readonly ProviderConfiguration s_defaultBroadcastChannel = new("Default");
    private static readonly ProviderConfiguration s_developmentClustering = new("Development");

    /// <summary>
    /// Adds an Orleans service to the application.
    /// </summary>
    /// <param name="builder">The application builder.</param>
    /// <param name="name">The name of the Orleans service.</param>
    /// <returns>The Orleans service builder.</returns>
    [AspireExport("addOrleans", Description = "Adds an Orleans service configuration")]
    public static OrleansService AddOrleans(
        this IDistributedApplicationBuilder builder,
        string name)
        => new(builder, name);

    /// <summary>
    /// Sets the ClusterId of the Orleans service.
    /// </summary>
    /// <param name="orleansServiceBuilder">The target Orleans service builder.</param>
    /// <param name="clusterId">The ClusterId value.</param>
    /// <returns>The Orleans service builder.</returns>
    [AspireExport("withClusterId", Description = "Sets the Orleans cluster ID")]
    public static OrleansService WithClusterId(
        this OrleansService orleansServiceBuilder,
        string clusterId)
    {
        orleansServiceBuilder.ClusterId = clusterId;
        return orleansServiceBuilder;
    }

    /// <summary>
    /// Sets the ClusterId of the Orleans service.
    /// </summary>
    /// <param name="orleansServiceBuilder">The target Orleans service builder.</param>
    /// <param name="clusterId">The ClusterId value.</param>
    /// <returns>The Orleans service builder.</returns>
    /// <remarks>This method is not available in polyglot app hosts. Use <see cref="WithClusterId(OrleansService, string)"/> instead.</remarks>
    [AspireExportIgnore(Reason = "ParameterResource handle overload is not needed in polyglot. Use the string overload instead.")]
    public static OrleansService WithClusterId(
        this OrleansService orleansServiceBuilder,
        IResourceBuilder<ParameterResource> clusterId)
    {
        orleansServiceBuilder.ClusterId = clusterId.Resource;
        return orleansServiceBuilder;
    }

    /// <summary>
    /// Sets the ServiceId of the Orleans service.
    /// </summary>
    /// <param name="orleansServiceBuilder">The target Orleans service builder.</param>
    /// <param name="serviceId">The ServiceId value.</param>
    /// <returns>The Orleans service builder.</returns>
    [AspireExport("withServiceId", Description = "Sets the Orleans service ID")]
    public static OrleansService WithServiceId(
        this OrleansService orleansServiceBuilder,
        string serviceId)
    {
        orleansServiceBuilder.ServiceId = serviceId;
        return orleansServiceBuilder;
    }

    /// <summary>
    /// Sets the ServiceId of the Orleans service.
    /// </summary>
    /// <param name="orleansServiceBuilder">The target Orleans service builder.</param>
    /// <param name="serviceId">The ServiceId value.</param>
    /// <returns>The Orleans service builder.</returns>
    /// <remarks>This method is not available in polyglot app hosts. Use <see cref="WithServiceId(OrleansService, string)"/> instead.</remarks>
    [AspireExportIgnore(Reason = "ParameterResource handle overload is not needed in polyglot. Use the string overload instead.")]
    public static OrleansService WithServiceId(
        this OrleansService orleansServiceBuilder,
        IResourceBuilder<ParameterResource> serviceId)
    {
        orleansServiceBuilder.ServiceId = serviceId.Resource;
        return orleansServiceBuilder;
    }

    /// <summary>
    /// Configures the Orleans service to use the provided clustering provider.
    /// </summary>
    /// <param name="orleansServiceBuilder">The target Orleans service builder.</param>
    /// <param name="provider">The provider.</param>
    /// <returns>The Orleans service builder.</returns>
    [AspireExport("withClustering", Description = "Configures Orleans clustering using a resource connection")]
    public static OrleansService WithClustering(
        this OrleansService orleansServiceBuilder,
        IResourceBuilder<IResourceWithConnectionString> provider)
        => WithClustering(orleansServiceBuilder, ProviderConfiguration.Create(provider));

    /// <summary>
    /// Configures the Orleans service to use the provided clustering provider.
    /// </summary>
    /// <param name="orleansServiceBuilder">The target Orleans service builder.</param>
    /// <param name="provider">The provider.</param>
    /// <returns>The Orleans service builder.</returns>
    /// <remarks>This method is not available in polyglot app hosts. Use <see cref="WithClustering(OrleansService, IResourceBuilder{IResourceWithConnectionString})"/> instead.</remarks>
    [AspireExportIgnore(Reason = "IProviderConfiguration cannot be created directly by polyglot callers. Use the resource-based overload instead.")]
    public static OrleansService WithClustering(
        this OrleansService orleansServiceBuilder,
        IProviderConfiguration provider)
    {
        orleansServiceBuilder.Clustering = provider;
        return orleansServiceBuilder;
    }

    /// <summary>
    /// Configures the Orleans service to use development-only clustering.
    /// </summary>
    /// <param name="orleansServiceBuilder">The target Orleans service builder.</param>
    /// <returns>The Orleans service builder.</returns>
    [AspireExport("withDevelopmentClustering", Description = "Configures Orleans development clustering")]
    public static OrleansService WithDevelopmentClustering(
        this OrleansService orleansServiceBuilder)
        => WithClustering(orleansServiceBuilder, s_developmentClustering);

    /// <summary>
    /// Adds a grain storage provider to the Orleans service.
    /// </summary>
    /// <param name="orleansServiceBuilder">The target Orleans service builder.</param>
    /// <param name="provider">The provider to add.</param>
    /// <returns>The Orleans service builder.</returns>
    /// <remarks>This resource name is the name the application will use to resolve the provider. This method is not available in polyglot app hosts. Use <see cref="WithGrainStorage(OrleansService, string, IResourceBuilder{IResourceWithConnectionString})"/> instead.</remarks>
    [AspireExportIgnore(Reason = "Convenience overload. Use the overload with explicit provider name instead.")]
    public static OrleansService WithGrainStorage(
        this OrleansService orleansServiceBuilder,
        IResourceBuilder<IResourceWithConnectionString> provider)
        => WithGrainStorage(orleansServiceBuilder, provider.Resource.Name, provider);

    /// <summary>
    /// Adds a grain storage provider to the Orleans service.
    /// </summary>
    /// <param name="orleansServiceBuilder">The target Orleans service builder.</param>
    /// <param name="name">The name of the provider. This is the name the application will use to resolve the provider.</param>
    /// <param name="provider">The provider to add.</param>
    /// <returns>The Orleans service builder.</returns>
    [AspireExport("withGrainStorage", Description = "Adds an Orleans grain storage provider")]
    public static OrleansService WithGrainStorage(
        this OrleansService orleansServiceBuilder,
        string name,
        IResourceBuilder<IResourceWithConnectionString> provider)
        => WithGrainStorage(orleansServiceBuilder, name, ProviderConfiguration.Create(provider));

    /// <summary>
    /// Adds a grain storage provider to the Orleans service.
    /// </summary>
    /// <param name="orleansServiceBuilder">The target Orleans service builder.</param>
    /// <param name="name">The name of the provider. This is the name the application will use to resolve the provider.</param>
    /// <param name="provider">The provider to add.</param>
    /// <returns>The Orleans service builder.</returns>
    /// <remarks>This method is not available in polyglot app hosts. Use <see cref="WithGrainStorage(OrleansService, string, IResourceBuilder{IResourceWithConnectionString})"/> instead.</remarks>
    [AspireExportIgnore(Reason = "IProviderConfiguration cannot be created directly by polyglot callers. Use the resource-based overload instead.")]
    public static OrleansService WithGrainStorage(
        this OrleansService orleansServiceBuilder,
        string name,
        IProviderConfiguration provider)
    {
        orleansServiceBuilder.GrainStorage[name] = provider;
        return orleansServiceBuilder;
    }

    /// <summary>
    /// Adds an in-memory grain storage to the Orleans service.
    /// </summary>
    /// <param name="orleansServiceBuilder">The target Orleans service builder.</param>
    /// <param name="name">The name of the provider. This is the name the application will use to resolve the provider.</param>
    /// <returns>The Orleans service builder.</returns>
    [AspireExport("withMemoryGrainStorage", Description = "Adds in-memory Orleans grain storage")]
    public static OrleansService WithMemoryGrainStorage(
        this OrleansService orleansServiceBuilder,
        string name)
        => WithGrainStorage(orleansServiceBuilder, name, s_inMemoryGrainStorage);

    /// <summary>
    /// Adds a stream provider to the Orleans service.
    /// </summary>
    /// <param name="orleansServiceBuilder">The target Orleans service builder.</param>
    /// <param name="provider">The provider to add.</param>
    /// <returns>The Orleans service builder.</returns>
    /// <remarks>This resource name is the name the application will use to resolve the provider. This method is not available in polyglot app hosts. Use <see cref="WithStreaming(OrleansService, string, IResourceBuilder{IResourceWithConnectionString})"/> instead.</remarks>
    [AspireExportIgnore(Reason = "Convenience overload. Use the overload with explicit provider name instead.")]
    public static OrleansService WithStreaming(
        this OrleansService orleansServiceBuilder,
        IResourceBuilder<IResourceWithConnectionString> provider)
        => WithStreaming(orleansServiceBuilder, provider.Resource.Name, provider);

    /// <summary>
    /// Adds a stream provider to the Orleans service.
    /// </summary>
    /// <param name="orleansServiceBuilder">The target Orleans service builder.</param>
    /// <param name="name">The name of the provider. This is the name the application will use to resolve the provider.</param>
    /// <param name="provider">The provider to add.</param>
    /// <returns>The Orleans service builder.</returns>
    [AspireExport("withStreaming", Description = "Adds an Orleans stream provider")]
    public static OrleansService WithStreaming(
        this OrleansService orleansServiceBuilder,
        string name,
        IResourceBuilder<IResourceWithConnectionString> provider)
        => WithStreaming(orleansServiceBuilder, name, ProviderConfiguration.Create(provider));

    /// <summary>
    /// Adds a stream provider to the Orleans service.
    /// </summary>
    /// <param name="orleansServiceBuilder">The target Orleans service builder.</param>
    /// <param name="name">The name of the provider. This is the name the application will use to resolve the provider.</param>
    /// <param name="provider">The provider to add.</param>
    /// <returns>The Orleans service builder.</returns>
    /// <remarks>This method is not available in polyglot app hosts. Use <see cref="WithStreaming(OrleansService, string, IResourceBuilder{IResourceWithConnectionString})"/> instead.</remarks>
    [AspireExportIgnore(Reason = "IProviderConfiguration cannot be created directly by polyglot callers. Use the resource-based overload instead.")]
    public static OrleansService WithStreaming(
        this OrleansService orleansServiceBuilder,
        string name,
        IProviderConfiguration provider)
    {
        orleansServiceBuilder.Streaming[name] = provider;
        return orleansServiceBuilder;
    }

    /// <summary>
    /// Adds an in-memory stream provider to the Orleans service.
    /// </summary>
    /// <param name="orleansServiceBuilder">The target Orleans service builder.</param>
    /// <param name="name">The name of the provider. This is the name the application will use to resolve the provider.</param>
    /// <returns>The Orleans service builder.</returns>
    [AspireExport("withMemoryStreaming", Description = "Adds in-memory Orleans streaming")]
    public static OrleansService WithMemoryStreaming(
        this OrleansService orleansServiceBuilder,
        string name)
        => WithStreaming(orleansServiceBuilder, name, s_inMemoryStreaming);

    /// <summary>
    /// Adds a broadcast channel provider to the Orleans service.
    /// </summary>
    /// <param name="orleansServiceBuilder">The target Orleans service builder.</param>
    /// <param name="name">The name of the provider. This is the name the application will use to resolve the provider.</param>
    /// <param name="provider">The provider to add.</param>
    /// <returns>The Orleans service builder.</returns>
    /// <remarks>This method is not available in polyglot app hosts. Use <see cref="WithBroadcastChannel(OrleansService, string)"/> instead.</remarks>
    [AspireExportIgnore(Reason = "IProviderConfiguration cannot be created directly by polyglot callers. Use the default provider overload instead.")]
    public static OrleansService WithBroadcastChannel(
        this OrleansService orleansServiceBuilder,
        string name,
        IProviderConfiguration provider)
    {
        orleansServiceBuilder.BroadcastChannel[name] = provider;
        return orleansServiceBuilder;
    }

    /// <summary>
    /// Adds a broadcast channel provider to the Orleans service.
    /// </summary>
    /// <param name="orleansServiceBuilder">The target Orleans service builder.</param>
    /// <param name="name">The name of the provider. This is the name the application will use to resolve the provider.</param>
    /// <returns>The Orleans service builder.</returns>
    [AspireExport("withBroadcastChannel", Description = "Adds an Orleans broadcast channel provider")]
    public static OrleansService WithBroadcastChannel(
        this OrleansService orleansServiceBuilder,
        string name)
        => WithBroadcastChannel(orleansServiceBuilder, name, s_defaultBroadcastChannel);

    /// <summary>
    /// Configures reminder storage for the Orleans service.
    /// </summary>
    /// <param name="orleansServiceBuilder">The target Orleans service builder.</param>
    /// <param name="provider">The reminder storage provider.</param>
    /// <returns>The Orleans service builder.</returns>
    [AspireExport("withReminders", Description = "Configures Orleans reminder storage")]
    public static OrleansService WithReminders(
        this OrleansService orleansServiceBuilder,
        IResourceBuilder<IResourceWithConnectionString> provider)
        => WithReminders(orleansServiceBuilder, ProviderConfiguration.Create(provider));

    /// <summary>
    /// Configures reminder storage for the Orleans service.
    /// </summary>
    /// <param name="orleansServiceBuilder">The target Orleans service builder.</param>
    /// <param name="provider">The reminder storage provider to use.</param>
    /// <returns>The Orleans service builder.</returns>
    /// <remarks>This method is not available in polyglot app hosts. Use <see cref="WithReminders(OrleansService, IResourceBuilder{IResourceWithConnectionString})"/> instead.</remarks>
    [AspireExportIgnore(Reason = "IProviderConfiguration cannot be created directly by polyglot callers. Use the resource-based overload instead.")]
    public static OrleansService WithReminders(
        this OrleansService orleansServiceBuilder,
        IProviderConfiguration provider)
    {
        orleansServiceBuilder.Reminders = provider;
        return orleansServiceBuilder;
    }

    /// <summary>
    /// Configures in-memory reminder storage for the Orleans service.
    /// </summary>
    /// <param name="orleansServiceBuilder">The target Orleans service builder.</param>
    /// <returns>The Orleans service builder.</returns>
    [AspireExport("withMemoryReminders", Description = "Configures in-memory Orleans reminders")]
    public static OrleansService WithMemoryReminders(
        this OrleansService orleansServiceBuilder)
    {
        orleansServiceBuilder.Reminders = s_inMemoryReminderService;
        return orleansServiceBuilder;
    }

    /// <summary>
    /// Adds a grain directory provider to the Orleans service.
    /// </summary>
    /// <param name="orleansServiceBuilder">The target Orleans service builder.</param>
    /// <param name="provider">The provider to add.</param>
    /// <returns>The Orleans service builder.</returns>
    /// <remarks>This resource name is the name the application will use to resolve the provider. This method is not available in polyglot app hosts. Use <see cref="WithGrainDirectory(OrleansService, string, IResourceBuilder{IResourceWithConnectionString})"/> instead.</remarks>
    [AspireExportIgnore(Reason = "Convenience overload. Use the overload with explicit provider name instead.")]
    public static OrleansService WithGrainDirectory(
        this OrleansService orleansServiceBuilder,
        IResourceBuilder<IResourceWithConnectionString> provider)
        => WithGrainDirectory(orleansServiceBuilder, provider.Resource.Name, provider);

    /// <summary>
    /// Adds a grain directory provider to the Orleans service.
    /// </summary>
    /// <param name="orleansServiceBuilder">The target Orleans service builder.</param>
    /// <param name="name">The name of the provider. This is the name the application will use to resolve the provider.</param>
    /// <param name="provider">The provider to add.</param>
    /// <returns>The Orleans service builder.</returns>
    [AspireExport("withGrainDirectory", Description = "Adds an Orleans grain directory provider")]
    public static OrleansService WithGrainDirectory(
        this OrleansService orleansServiceBuilder,
        string name,
        IResourceBuilder<IResourceWithConnectionString> provider)
        => WithGrainDirectory(orleansServiceBuilder, name, ProviderConfiguration.Create(provider));

    /// <summary>
    /// Adds a grain directory provider to the Orleans service.
    /// </summary>
    /// <param name="orleansServiceBuilder">The target Orleans service builder.</param>
    /// <param name="name">The name of the provider. This is the name the application will use to resolve the provider.</param>
    /// <param name="provider">The provider to add.</param>
    /// <returns>The Orleans service builder.</returns>
    /// <remarks>This method is not available in polyglot app hosts. Use <see cref="WithGrainDirectory(OrleansService, string, IResourceBuilder{IResourceWithConnectionString})"/> instead.</remarks>
    [AspireExportIgnore(Reason = "IProviderConfiguration cannot be created directly by polyglot callers. Use the resource-based overload instead.")]
    public static OrleansService WithGrainDirectory(
        this OrleansService orleansServiceBuilder,
        string name,
        IProviderConfiguration provider)
    {
        orleansServiceBuilder.GrainDirectory[name] = provider;
        return orleansServiceBuilder;
    }

    /// <summary>
    /// Returns a model of the clients of an Orleans service.
    /// </summary>
    /// <param name="orleansService">The Orleans service</param>
    /// <returns>A model of the clients of an Orleans service.</returns>
    [AspireExport("asClient", Description = "Creates an Orleans client view for the service")]
    public static OrleansServiceClient AsClient(this OrleansService orleansService)
    {
        return new OrleansServiceClient(orleansService);
    }

    /// <summary>
    /// Adds Orleans to the resource.
    /// </summary>
    /// <param name="builder">The builder on which add the Orleans service builder.</param>
    /// <param name="orleansService">The Orleans service, containing clustering, etc.</param>
    /// <returns>The resource builder.</returns>
    /// <exception cref="InvalidOperationException">Clustering has not been configured.</exception>
    [AspireExport("withOrleansReference", Description = "Adds an Orleans silo reference to a resource")]
    public static IResourceBuilder<T> WithReference<T>(
        this IResourceBuilder<T> builder,
        OrleansService orleansService)
        where T : IResourceWithEnvironment, IResourceWithEndpoints
    {
        return builder.WithOrleansReference(orleansService, isSilo: true);
    }

    internal static IResourceBuilder<T> WithOrleansReference<T>(
        this IResourceBuilder<T> builder,
        OrleansService orleansService,
        bool isSilo)
        where T : IResourceWithEnvironment, IResourceWithEndpoints
    {
        var res = orleansService;

        // Configure clustering
        if (res.Clustering is { } clustering)
        {
            clustering.ConfigureResource(builder, "Clustering");
        }
        else
        {
            throw new InvalidOperationException("Clustering has not been configured for this service.");
        }

        foreach (var (name, provider) in res.Streaming)
        {
            provider.ConfigureResource(builder, $"Streaming__{name}");
        }

        foreach (var (name, provider) in res.BroadcastChannel)
        {
            provider.ConfigureResource(builder, $"BroadcastChannel__{name}");
        }

        builder.WithEnvironment(context =>
        {
            context.EnvironmentVariables["Orleans__ClusterId"] = res.ClusterId;
            context.EnvironmentVariables["Orleans__ServiceId"] = res.ServiceId;

            // Enable distributed tracing by default
            if (res.EnableDistributedTracing != false)
            {
                context.EnvironmentVariables["Orleans__EnableDistributedTracing"] = "true";
            }
        });

        if (isSilo)
        {
            if (res.Reminders is { } reminders)
            {
                reminders.ConfigureResource(builder, "Reminders");
            }

            foreach (var (name, provider) in res.GrainStorage)
            {
                provider.ConfigureResource(builder, $"GrainStorage__{name}");
            }

            foreach (var (name, provider) in res.GrainDirectory)
            {
                provider.ConfigureResource(builder, $"GrainDirectory__{name}");
            }

            // Set silo-to-silo and client-to-gateway ports
            // NOTE: These endpoints are specified as proxied even though we never expect any connections via the proxy, and that would be invalid anyway.
            // this is a workaround for current Aspire/DCP behavior.
            builder.WithEndpoint(scheme: "tcp", name: "orleans-silo", env: "Orleans__Endpoints__SiloPort", isProxied: true, isExternal: false);
            builder.WithEndpoint(scheme: "tcp", name: "orleans-gateway", env: "Orleans__Endpoints__GatewayPort", isProxied: true, isExternal: false);
        }

        return builder;
    }
}
