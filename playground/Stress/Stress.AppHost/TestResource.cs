// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Dashboard.Model;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Lifecycle;
using Microsoft.Extensions.Logging;

static class TestResourceExtensions
{
    [AspireExportIgnore(Reason = "Stress playground helper; not part of the supported ATS surface.")]
    public static IResourceBuilder<TestResource> AddTestResource(this IDistributedApplicationBuilder builder, string name)
    {
        builder.Services.TryAddEventingSubscriber<TestResourceLifecycle>();

        var rb = builder.AddResource(new TestResource(name))
                      .WithInitialState(new()
                      {
                          ResourceType = "Test Resource",
                          State = "Starting",
                          Properties = [
                              new("P1", "P2"),
                              new(CustomResourceKnownProperties.Source, "Custom")
                          ]
                      })
                      .ExcludeFromManifest();

        return rb;
    }

    [AspireExportIgnore(Reason = "Stress playground helper; not part of the supported ATS surface.")]
    public static IResourceBuilder<TestNestedResource> AddNestedResource(this IDistributedApplicationBuilder builder, string name, IResource parent)
    {
        var rb = builder.AddResource(new TestNestedResource(name, parent))
                      .WithInitialState(new()
                      {
                          ResourceType = "Test Nested Resource",
                          State = "Starting",
                          Properties = [
                              new("P1", "P2"),
                              new(CustomResourceKnownProperties.Source, "Custom"),
                              new(KnownProperties.Resource.ParentName, parent.Name)
                          ]
                      })
                      .ExcludeFromManifest();

        return rb;
    }

    [AspireExportIgnore(Reason = "Stress playground helper; not part of the supported ATS surface.")]
    public static IResourceBuilder<CommandGroupResource> AddCommandGroup(this IDistributedApplicationBuilder builder, string name, IResource parent)
    {
        var rb = builder.AddResource(new CommandGroupResource(name, parent))
                      .WithInitialState(new()
                      {
                          ResourceType = "Command Group",
                          State = "Running",
                          Properties = [
                              new(KnownProperties.Resource.ParentName, parent.Name)
                          ]
                      })
                      .ExcludeFromManifest();

        return rb;
    }

    [AspireExportIgnore(Reason = "Stress playground helper; not part of the supported ATS surface.")]
    public static IResourceBuilder<NoStatusResource> AddNoStatusResource(this IDistributedApplicationBuilder builder, string name)
    {
        var rb = builder.AddResource(new NoStatusResource(name))
                      .WithInitialState(new()
                      {
                          ResourceType = "No Status Resource",
                          Properties = [
                              new(CustomResourceKnownProperties.Source, "Custom")
                          ]
                      })
                      .ExcludeFromManifest();

        return rb;
    }

    [AspireExportIgnore(Reason = "Stress playground helper; not part of the supported ATS surface.")]
    public static IResourceBuilder<PropertyStressResource> AddPropertyStressResource(this IDistributedApplicationBuilder builder, string name)
    {
        var rb = builder.AddResource(new PropertyStressResource(name))
                      .WithInitialState(new()
                      {
                          ResourceType = "Executable",
                          State = "Running",
                          Properties = [
                              new(KnownProperties.Executable.Path, "/stress/known/path")
                              {
                                  DisplayName = "Known non-sensitive path"
                              },
                              new(KnownProperties.Executable.Args, "--api-key stress-secret-value")
                              {
                                  DisplayName = "Known sensitive arguments",
                                  IsSensitive = true
                              },
                              new("stress.property.highlighted", "Visible highlighted value")
                              {
                                  DisplayName = "Unknown highlighted property",
                                  IsHighlighted = true
                              },
                              new("stress.property.highlightedSecret", "Visible highlighted sensitive value")
                              {
                                  DisplayName = "Unknown highlighted sensitive property",
                                  IsSensitive = true,
                                  IsHighlighted = true
                              },
                              new("stress.property.hidden", "Hidden until Show all is selected")
                              {
                                  DisplayName = "Unknown non-highlighted property"
                              },
                              new("stress.property.hiddenSecret", "Hidden sensitive value until Show all is selected")
                              {
                                  DisplayName = "Unknown non-highlighted sensitive property",
                                  IsSensitive = true
                              }
                          ]
                      })
                      .ExcludeFromManifest();

        return rb;
    }
}

internal sealed class TestResourceLifecycle(
    ResourceNotificationService notificationService,
    ResourceLoggerService loggerService
    ) : IDistributedApplicationEventingSubscriber, IAsyncDisposable
{
    private readonly CancellationTokenSource _tokenSource = new();

    public Task OnBeforeStartAsync(BeforeStartEvent @event, CancellationToken cancellationToken = default)
    {
        foreach (var resource in @event.Model.Resources.OfType<TestResource>())
        {
            var states = new[] { "Starting", "Running", "Finished" };

            var logger = loggerService.GetLogger(resource);

            Task.Run(async () =>
            {
                var seconds = Random.Shared.Next(2, 12);

                logger.LogInformation("Starting test resource {ResourceName} with update interval {Interval} seconds", resource.Name, seconds);

                await notificationService.PublishUpdateAsync(resource, state => state with
                {
                    Properties = [.. state.Properties, new("Interval", seconds.ToString(CultureInfo.InvariantCulture))]
                });

                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds));

                while (await timer.WaitForNextTickAsync(_tokenSource.Token))
                {
                    var randomState = states[Random.Shared.Next(0, states.Length)];

                    await notificationService.PublishUpdateAsync(resource, state => state with
                    {
                        State = randomState
                    });

                    logger.LogInformation("Test resource {ResourceName} is now in state {State}", resource.Name, randomState);
                }
            },
            cancellationToken);
        }

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _tokenSource.Cancel();
        return default;
    }

    public Task SubscribeAsync(IDistributedApplicationEventing eventing, DistributedApplicationExecutionContext executionContext, CancellationToken cancellationToken)
    {
        eventing.Subscribe<BeforeStartEvent>(OnBeforeStartAsync);
        return Task.CompletedTask;
    }
}

sealed class TestResource(string name) : Resource(name)
{

}

sealed class TestNestedResource(string name, IResource parent) : Resource(name), IResourceWithParent
{
    public IResource Parent { get; } = parent;
}

sealed class CommandGroupResource(string name, IResource parent) : Resource(name), IResourceWithParent
{
    public IResource Parent { get; } = parent;
}

sealed class NoStatusResource(string name) : Resource(name)
{
}

sealed class PropertyStressResource(string name) : Resource(name)
{
}
