// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.DashboardService.Proto.V1;
using Aspire.Tests.Shared.DashboardModel;
using Google.Protobuf.WellKnownTypes;

namespace Aspire.Dashboard.Tests.Integration.Playwright.Infrastructure;

public sealed class MockDashboardClient : IDashboardClient, IResourceRepositoryWriter
{
    public static readonly ResourceViewModel TestResource1 = ModelTestHelpers.CreateResource(
        resourceName: "TestResource",
        resourceType: KnownResourceTypes.Project,
        properties: new[]
        {
            new KeyValuePair<string, ResourcePropertyViewModel>(
                KnownProperties.Project.Path,
                new ResourcePropertyViewModel(
                    KnownProperties.Project.Path,
                    new Value()
                    {
                        StringValue = "C:/MyProjectPath/Project.csproj"
                    },
                    isValueSensitive: false,
                    knownProperty: new(KnownProperties.Project.Path, loc => "Path"),
                    sortOrder: 0,
                    displayName: null,
                    isHighlighted: false))
        }.ToDictionary(),
        state: KnownResourceState.Running);

    private readonly IReadOnlyList<ResourceViewModel>? _resources;

    public MockDashboardClient(IReadOnlyList<ResourceViewModel>? resources = null)
    {
        _resources = resources;
    }

    public bool IsEnabled => true;
    public Task WhenConnected => Task.CompletedTask;
    public string ApplicationName => "IntegrationTestApplication";
    public string? MinRequiredVersion => null;
    public DashboardConnectionState ConnectionState => DashboardConnectionState.Connected;
#pragma warning disable CS0067 // Event is never used - required by interface
    public event Action<DashboardConnectionState>? ConnectionStateChanged;
#pragma warning restore CS0067
    public Task ReconnectAsync() => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public Task<ResourceCommandResponseViewModel> ExecuteResourceCommandAsync(string resourceName, string resourceType, CommandViewModel command, ExecuteResourceCommandOptions options, CancellationToken cancellationToken) => throw new NotImplementedException();
    public Task<string> UploadFileAsync(Stream fileStream, string fileName, long expectedSize, int interactionId, string inputName, CancellationToken cancellationToken) => throw new NotImplementedException();
    public Task<Stream> AttachTerminalAsync(string terminalId, CancellationToken cancellationToken) => throw new NotImplementedException();
    public IAsyncEnumerable<WatchTerminalsUpdate> SubscribeTerminalsAsync(CancellationToken cancellationToken) => throw new NotImplementedException();
    public Task<TerminalDescriptor> CreateDockTerminalAsync(string? title, CancellationToken cancellationToken) => throw new NotImplementedException();
    public Task CloseTerminalAsync(string terminalId, CancellationToken cancellationToken) => throw new NotImplementedException();
    public async IAsyncEnumerable<IReadOnlyList<ResourceLogLine>> SubscribeConsoleLogs(string resourceName, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        yield break;
    }

    public async IAsyncEnumerable<IReadOnlyList<ResourceLogLine>> GetConsoleLogs(string resourceName, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        yield break;
    }

    /// <inheritdoc/>
    public Task ClearConsoleLogsAsync(IReadOnlyList<string> resourceNames, DateTime clearDate) => Task.CompletedTask;

    public Task<ResourceViewModelSubscription> SubscribeResourcesAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult(new ResourceViewModelSubscription(
            [.. (_resources ?? [TestResource1])],
            Test()
        ));
    }

    private static async IAsyncEnumerable<IReadOnlyList<ResourceViewModelChange>> Test()
    {
        await Task.CompletedTask;
        yield return [];
    }

    public IAsyncEnumerable<WatchInteractionsResponseUpdate> SubscribeInteractionsAsync(CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    public Task SendInteractionRequestAsync(WatchInteractionsRequestUpdate request, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    public ResourceViewModel? GetResource(string resourceName) => null;

    public IReadOnlyList<ResourceViewModel> GetResources() => _resources ?? [];

    public Task ReplaceResourcesAsync(IReadOnlyList<Resource> resources) => Task.CompletedTask;

    public Task ApplyChangesAsync(IReadOnlyList<WatchResourcesChange> changes) => Task.CompletedTask;

    public Task MarkConsoleLogsLoadedAsync(string resourceName) => Task.CompletedTask;

    public Task AddConsoleLogsAsync(string resourceName, IReadOnlyList<ConsoleLogLine> logLines) => Task.CompletedTask;
}

internal sealed class MockRepositoryFactory(
    IServiceProvider serviceProvider,
    IResourceRepository resourceRepository) : IRepositoryFactory
{
    private readonly RepositoryFactory _inner = new(serviceProvider);

    public ITelemetryRepository CreateTelemetryRepository(DashboardSqliteDatabase database) =>
        _inner.CreateTelemetryRepository(database);

    public IResourceRepository CreateResourceRepository(DashboardSqliteDatabase database) =>
        resourceRepository;
}
