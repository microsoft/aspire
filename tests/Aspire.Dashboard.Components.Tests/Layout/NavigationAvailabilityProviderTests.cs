// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Channels;
using Aspire.Dashboard.Components.Layout;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.Tests.Shared;
using Aspire.Tests.Shared.DashboardModel;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Proto.Logs.V1;
using Xunit;
using static Aspire.Tests.Shared.Telemetry.TelemetryTestHelpers;

namespace Aspire.Dashboard.Components.Tests.Layout;

public class NavigationAvailabilityProviderTests : DashboardTestContext
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void DisabledOrHistorical_DoesNotSubscribe(bool enabled, bool readOnly)
    {
        FluentUISetupHelpers.AddCommonDashboardServices(this);
        var client = new TestDashboardClient(isEnabled: enabled, isReadOnly: readOnly);
        Services.AddSingleton<IDashboardClient>(client);
        var cut = Render<NavigationAvailabilityProvider>(builder => builder.Add(p => p.AvailabilityChanged, _ => { }));
        Assert.Equal(0, client.ResourceSubscriptionCount);
    }

    [Fact]
    public async Task DisposalDuringConnection_CancelsWithoutStartingWatch()
    {
        FluentUISetupHelpers.AddCommonDashboardServices(this);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new TestDashboardClient(isEnabled: true, whenConnected: connected.Task);
        Services.AddSingleton<IDashboardClient>(client);
        var cut = Render<NavigationAvailabilityProvider>(builder => builder.Add(p => p.AvailabilityChanged, _ => { }));
        await cut.InvokeAsync(() => cut.Instance.DisposeAsync().AsTask());
        connected.SetResult();
        Assert.Equal(0, client.ResourceSubscriptionCount);
    }

    [Fact]
    public async Task ReportsParametersAndTelemetrySourcesOnceTheAppModelIsLoaded()
    {
        FluentUISetupHelpers.AddCommonDashboardServices(this);
        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        var client = new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("api")],
            resourceChannelProvider: () => channel);
        Services.AddSingleton<IDashboardClient>(client);
        // "api" sends telemetry as part of the app model, so it isn't a telemetry source.
        await AddTelemetryAsync("api");
        // The provider renders nothing, so its reports are awaited rather than checked after a render.
        var reported = Channel.CreateUnbounded<NavigationAvailability>();

        Render<NavigationAvailabilityProvider>(builder => builder.Add(p => p.AvailabilityChanged, availability => reported.Writer.TryWrite(availability)));

        Assert.Equal(
            new NavigationAvailability(HasResourceTerminals: false, HasParameters: false, HasTelemetrySources: false),
            await reported.Reader.ReadAsync().AsTask().WaitAsync(DefaultWaitTimeout));

        await channel.Writer.WriteAsync([new(ResourceViewModelChangeType.Upsert, ModelTestHelpers.CreateResource("secret", resourceType: KnownResourceTypes.Parameter))]);
        Assert.Equal(
            new NavigationAvailability(HasResourceTerminals: false, HasParameters: true, HasTelemetrySources: false),
            await reported.Reader.ReadAsync().AsTask().WaitAsync(DefaultWaitTimeout));

        await AddTelemetryAsync("browser");
        Assert.Equal(
            new NavigationAvailability(HasResourceTerminals: false, HasParameters: true, HasTelemetrySources: true),
            await reported.Reader.ReadAsync().AsTask().WaitAsync(DefaultWaitTimeout));
    }

    private Task AddTelemetryAsync(string name)
    {
        var resource = CreateResource(name);
        resource.Attributes.Remove(resource.Attributes.Single(attribute => attribute.Key == OtlpResource.SERVICE_INSTANCE_ID));
        return Services.GetRequiredService<SqliteTelemetryRepository>().AddLogsAsync(new AddContext(),
        [
            new ResourceLogs
            {
                Resource = resource,
                ScopeLogs = { new ScopeLogs { Scope = CreateScope(), LogRecords = { CreateLogRecord() } } }
            }
        ]);
    }
}
