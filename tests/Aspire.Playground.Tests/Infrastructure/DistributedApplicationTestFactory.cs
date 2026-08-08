// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Lifecycle;
using Microsoft.Extensions.DependencyInjection;
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
    /// resources. Use <see cref="CreateWithArgsAsync"/> when the AppHost reads a configuration value at
    /// construction time.
    /// </remarks>
    public static Task<IDistributedApplicationTestingBuilder> CreateAsync(Type appHostProgramType, ITestOutputHelper? testOutput, Action<IDistributedApplicationTestingBuilder>? configureBuilder = null)
        => CreateCoreAsync(appHostProgramType, testOutput, args: [], configureBuilder);

    /// <summary>
    /// Creates an <see cref="IDistributedApplicationTestingBuilder"/> for the specified app host assembly, passing
    /// <paramref name="args"/> to the AppHost as command-line arguments.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Command-line arguments are the correct way to seed configuration an AppHost reads while it builds its resources
    /// (for example <c>builder.Configuration["SOME_TOOL_PATH"]</c>, <c>AppHost:Operation</c>, or
    /// <c>Publishing:Publisher</c>). They are applied before Program.cs runs, and the command-line configuration
    /// provider is added last, so they outrank ambient environment variables of the test process.
    /// </para>
    /// <para>
    /// Args are additive: <c>DistributedApplicationFactory</c> appends them to
    /// <c>HostApplicationBuilderSettings.Args</c> and leaves <c>HostApplicationBuilderSettings.Configuration</c>
    /// untouched, so the testing defaults it seeds there (<c>DcpPublisher:RandomizePorts</c>,
    /// <c>DcpPublisher:WaitForResourceCleanup</c>, the container runtime timeout, dashboard/OTLP URLs and
    /// unsecured transport) are all retained. Assigning <c>settings.Configuration</c> would discard them.
    /// </para>
    /// </remarks>
    public static Task<IDistributedApplicationTestingBuilder> CreateWithArgsAsync(Type appHostProgramType, ITestOutputHelper? testOutput, string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        return CreateCoreAsync(appHostProgramType, testOutput, args, configureBuilder: null);
    }

    private static async Task<IDistributedApplicationTestingBuilder> CreateCoreAsync(
        Type appHostProgramType,
        ITestOutputHelper? testOutput,
        string[] args,
        Action<IDistributedApplicationTestingBuilder>? configureBuilder)
    {
        // DistributedApplicationTestingBuilder.CreateAsync(Type) forwards to CreateAsync(entryPoint, [], ct),
        // so passing empty args here is the same code path the no-args callers had before.
        var builder = await DistributedApplicationTestingBuilder.CreateAsync(appHostProgramType, args);

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
