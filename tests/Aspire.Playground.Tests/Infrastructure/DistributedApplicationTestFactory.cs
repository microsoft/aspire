// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Lifecycle;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SamplesIntegrationTests.Infrastructure;
using Xunit;

namespace SamplesIntegrationTests;

internal static class DistributedApplicationTestFactory
{
    /// <summary>
    /// Creates an <see cref="IDistributedApplicationTestingBuilder"/> for the specified app host assembly.
    /// </summary>
    /// <remarks>
    /// <paramref name="configureBuilder"/> runs *after* the AppHost's Program.cs has already executed, so it can only
    /// mutate the built application model. It cannot influence configuration the AppHost reads while constructing
    /// resources. Use <see cref="CreateWithHostSettingsAsync"/> when the AppHost reads a configuration value at
    /// construction time.
    /// </remarks>
    public static Task<IDistributedApplicationTestingBuilder> CreateAsync(Type appHostProgramType, ITestOutputHelper? testOutput, Action<IDistributedApplicationTestingBuilder>? configureBuilder = null)
        => CreateCoreAsync(appHostProgramType, testOutput, configureHostSettings: null, configureBuilder);

    /// <summary>
    /// Creates an <see cref="IDistributedApplicationTestingBuilder"/> for the specified app host assembly, applying
    /// <paramref name="configureHostSettings"/> *before* the AppHost's Program.cs runs.
    /// </summary>
    /// <remarks>
    /// This is the only hook that can seed configuration an AppHost reads while it builds its resources (for example
    /// <c>builder.Configuration["SOME_TOOL_PATH"]</c> or <c>AppHost:Operation</c>). Seeding that configuration through
    /// the post-build <c>configureBuilder</c> callback is silently ineffective.
    /// </remarks>
    public static Task<IDistributedApplicationTestingBuilder> CreateWithHostSettingsAsync(
        Type appHostProgramType,
        ITestOutputHelper? testOutput,
        Action<DistributedApplicationOptions, HostApplicationBuilderSettings> configureHostSettings,
        Action<IDistributedApplicationTestingBuilder>? configureBuilder = null)
    {
        ArgumentNullException.ThrowIfNull(configureHostSettings);

        return CreateCoreAsync(appHostProgramType, testOutput, configureHostSettings, configureBuilder);
    }

    private static async Task<IDistributedApplicationTestingBuilder> CreateCoreAsync(
        Type appHostProgramType,
        ITestOutputHelper? testOutput,
        Action<DistributedApplicationOptions, HostApplicationBuilderSettings>? configureHostSettings,
        Action<IDistributedApplicationTestingBuilder>? configureBuilder)
    {
        var builder = configureHostSettings is null
            ? await DistributedApplicationTestingBuilder.CreateAsync(appHostProgramType)
            : await DistributedApplicationTestingBuilder.CreateAsync(appHostProgramType, [], configureHostSettings);

        // Custom hook needed because we want to only override the registry when
        // the original is from `docker.io`, but the options.ContainerRegistryOverride will
        // always override.
        builder.Services.AddEventingSubscriber<ContainerRegistryHook>();

        builder.WithRandomParameterValues();
        builder.WithRandomVolumeNames();

        configureBuilder?.Invoke(builder);

        builder.Services.AddLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddSimpleConsole(configure =>
            {
                configure.SingleLine = true;
            });
            logging.AddFakeLogging();
            if (testOutput is not null)
            {
                logging.AddXunit(testOutput);
            }
            logging.SetMinimumLevel(LogLevel.Trace);
            logging.AddFilter("Aspire", LogLevel.Trace);
            logging.AddFilter(builder.Environment.ApplicationName, LogLevel.Trace);
        });

        return builder;
    }

    internal sealed class ContainerRegistryHook : IDistributedApplicationEventingSubscriber
    {
        public const string AspireTestContainerRegistry = "netaspireci.azurecr.io";

        public Task OnBeforeStartAsync(BeforeStartEvent @event, CancellationToken cancellationToken = default)
        {
            var resourcesWithContainerImages = @event.Model.Resources
                                                       .SelectMany(r => r.Annotations.OfType<ContainerImageAnnotation>()
                                                                                     .Select(cia => new { Resource = r, Annotation = cia }));

            foreach (var resourceWithContainerImage in resourcesWithContainerImages)
            {
                string? registry = resourceWithContainerImage.Annotation.Registry;
                if (registry is null || registry.Contains("docker.io"))
                {
                    resourceWithContainerImage.Annotation.Registry = AspireTestContainerRegistry;
                }
            }

            return Task.CompletedTask;
        }

        public Task SubscribeAsync(IDistributedApplicationEventing eventing, Aspire.Hosting.DistributedApplicationExecutionContext executionContext, CancellationToken cancellationToken)
        {
            eventing.Subscribe<BeforeStartEvent>(OnBeforeStartAsync);
            return Task.CompletedTask;
        }
    }

}
