// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.Security;
using System.Runtime.CompilerServices;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Utils;
using Aspire.DashboardService.Proto.V1;
using Aspire.Hosting;
using Grpc.Core;
using Grpc.Net.Client;
using Grpc.Net.Client.Configuration;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using Semver;
using DashboardResources = Aspire.Dashboard.Resources.Resources;
using ResourceCommandResponseKind = Aspire.Dashboard.Model.ResourceCommandResponseKind;

namespace Aspire.Dashboard.ServiceClient;

/// <summary>
/// Implements gRPC client that communicates with a resource server, populating data for the dashboard.
/// </summary>
/// <remarks>
/// <para>
/// An instance of this type is created per service call, so this class should not hold onto any state
/// expected to live longer than a single RPC request. In the case of streaming requests, the instance
/// lives until the stream is closed.
/// </para>
/// <para>
/// If the <c>ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL</c> environment variable is not specified, then there's
/// no known endpoint to connect to, and this dashboard client will be disabled. Calls to
/// <see cref="IResourceRepository.SubscribeResourcesAsync"/> and <see cref="IResourceRepository.SubscribeConsoleLogs"/>
/// will throw if <see cref="IDashboardClient.IsEnabled"/> is <see langword="false"/>. Callers should
/// check this property first, before calling these methods.
/// </para>
/// </remarks>
internal sealed class DashboardClient : IDashboardClient
{
    private const string ApiKeyHeaderName = "x-resource-service-api-key";
    private const string TroubleshootingUrl = "https://aka.ms/aspire/dashboard-apphost-connection-failed";

    // The dashboard's own version, extracted from its assembly at startup. Used to compare against
    // the minimum version required by the AppHost.
    private static readonly SemVersion? s_dashboardVersion = GetDashboardVersion();

    private readonly Dictionary<string, ResourceViewModel> _resourceByName = new(StringComparers.ResourceName);
    private readonly ActivitySource _activitySource;
    private readonly InteractionCollection _pendingInteractionCollection = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly CancellationToken _clientCancellationToken;
    private TaskCompletionSource _whenConnectedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _initialDataReceivedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<WatchInteractionsRequestUpdate> _incomingInteractionChannel = Channel.CreateUnbounded<WatchInteractionsRequestUpdate>();
    private readonly object _lock = new();
    private readonly TaskCompletionSource _resourceWatchCompleteTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _interactionWatchCompleteTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly ILoggerFactory _loggerFactory;
    private readonly IKnownPropertyLookup _knownPropertyLookup;
    private readonly DashboardOptions _dashboardOptions;
    private readonly IStringLocalizer<DashboardResources> _loc;
    private readonly ILogger<DashboardClient> _logger;
    private readonly IResourceRepositoryWriter? _resourceRepositoryWriter;

    private ImmutableHashSet<Channel<IReadOnlyList<ResourceViewModelChange>>> _outgoingResourceChannels = [];
    private ImmutableHashSet<Channel<WatchInteractionsResponseUpdate>> _outgoingInteractionChannels = [];
    private string? _applicationName;
    private string? _minRequiredVersion;

    private DashboardConnectionState _connectionState;
    private readonly object _connectionStateLock = new();
    private readonly object _reconnectDelayLock = new();
    private CancellationTokenSource? _reconnectDelayCts;

    private const int StateDisabled = -1;
    private const int StateNone = 0;
    private const int StateInitialized = 1;
    private const int StateDisposed = 2;
    private int _state = StateNone;

    private readonly GrpcChannel? _channel;
    internal Aspire.DashboardService.Proto.V1.DashboardService.DashboardServiceClient? _client;
    private readonly Metadata _headers = [];

    private Task? _connection;

    public DashboardClient(
        DashboardActivitySource activitySource,
        ILoggerFactory loggerFactory,
        IConfiguration configuration,
        IOptions<DashboardOptions> dashboardOptions,
        IKnownPropertyLookup knownPropertyLookup,
        IStringLocalizer<DashboardResources> loc,
        Action<SocketsHttpHandler>? configureHttpHandler = null,
        IResourceRepositoryWriter? resourceRepositoryWriter = null)
    {
        _activitySource = activitySource.ActivitySource;
        _loggerFactory = loggerFactory;
        _knownPropertyLookup = knownPropertyLookup;
        _dashboardOptions = dashboardOptions.Value;
        _loc = loc;
        _resourceRepositoryWriter = resourceRepositoryWriter;

        // Take a copy of the token and always use it to avoid race between disposal of CTS and usage of token.
        _clientCancellationToken = _cts.Token;

        _logger = loggerFactory.CreateLogger<DashboardClient>();

        if (dashboardOptions.Value.ResourceServiceClient.GetUri() is null)
        {
            _state = StateDisabled;
            _logger.LogDebug("{ConfigKey} is not specified. Dashboard client services are unavailable.", DashboardConfigNames.ResourceServiceUrlName.ConfigKey);
            _cts.Cancel();
            _whenConnectedTcs.TrySetCanceled();
            return;
        }

        var address = _dashboardOptions.ResourceServiceClient.GetUri()!;
        _logger.LogDebug("Dashboard configured to connect to: {Address}", address);

        // Create the gRPC channel. This channel performs automatic reconnects.
        // We will dispose it when we are disposed.
        _channel = CreateChannel();

        if (_dashboardOptions.ResourceServiceClient.AuthMode is ResourceClientAuthMode.ApiKey)
        {
            // We're using an API key for auth, so set it in the headers we pass on each call.
            _headers.Add(ApiKeyHeaderName, _dashboardOptions.ResourceServiceClient.ApiKey!);
        }

        _client = new Aspire.DashboardService.Proto.V1.DashboardService.DashboardServiceClient(_channel);

        GrpcChannel CreateChannel()
        {
            var httpHandler = new SocketsHttpHandler
            {
                EnableMultipleHttp2Connections = true,
                KeepAlivePingDelay = TimeSpan.FromSeconds(20),
                KeepAlivePingTimeout = TimeSpan.FromSeconds(10),
                KeepAlivePingPolicy = HttpKeepAlivePingPolicy.WithActiveRequests
            };

            var authMode = _dashboardOptions.ResourceServiceClient.AuthMode;

            if (authMode == ResourceClientAuthMode.Certificate)
            {
                // Auth hasn't been suppressed, so configure it.
                var certificates = _dashboardOptions.ResourceServiceClient.ClientCertificate.Source switch
                {
                    DashboardClientCertificateSource.File => GetFileCertificate(),
                    DashboardClientCertificateSource.KeyStore => GetKeyStoreCertificate(),
                    _ => throw new InvalidOperationException("Unable to load ResourceServiceClient client certificate.")
                };

                httpHandler.SslOptions = new SslClientAuthenticationOptions
                {
                    ClientCertificates = certificates
                };

                configuration.Bind("Dashboard:ResourceServiceClient:Ssl", httpHandler.SslOptions);
            }

            // https://learn.microsoft.com/aspnet/core/grpc/retries

            var methodConfig = new MethodConfig
            {
                Names = { MethodName.Default },
                RetryPolicy = new RetryPolicy
                {
                    MaxAttempts = 5,
                    InitialBackoff = TimeSpan.FromSeconds(1),
                    MaxBackoff = TimeSpan.FromSeconds(5),
                    BackoffMultiplier = 1.5,
                    RetryableStatusCodes = { StatusCode.Unavailable }
                }
            };

            configureHttpHandler?.Invoke(httpHandler);

            // https://learn.microsoft.com/aspnet/core/grpc/diagnostics#grpc-client-logging

            return GrpcChannel.ForAddress(
                address,
                channelOptions: new()
                {
                    HttpHandler = httpHandler,
                    ServiceConfig = new() { MethodConfigs = { methodConfig } },
                    LoggerFactory = _loggerFactory,
                    ThrowOperationCanceledOnCancellation = true,
                    MaxReceiveMessageSize = 16 * 1024 * 1024 // 16 MB
                });

            X509CertificateCollection GetFileCertificate()
            {
                Debug.Assert(
                    _dashboardOptions.ResourceServiceClient.ClientCertificate.FilePath != null,
                    "FilePath is validated as not null when configuration is loaded.");

                var filePath = _dashboardOptions.ResourceServiceClient.ClientCertificate.FilePath;
                var password = _dashboardOptions.ResourceServiceClient.ClientCertificate.Password;

                return [new X509Certificate2(filePath, password)];
            }

            X509CertificateCollection GetKeyStoreCertificate()
            {
                Debug.Assert(
                    _dashboardOptions.ResourceServiceClient.ClientCertificate.Subject != null,
                    "Subject is validated as not null when configuration is loaded.");

                var subject = _dashboardOptions.ResourceServiceClient.ClientCertificate.Subject;
                var storeName = _dashboardOptions.ResourceServiceClient.ClientCertificate.Store ?? "My";
                var location = _dashboardOptions.ResourceServiceClient.ClientCertificate.Location ?? StoreLocation.CurrentUser;

                using var store = new X509Store(storeName: storeName, storeLocation: location);

                store.Open(OpenFlags.ReadOnly);

                var certificates = store.Certificates.Find(X509FindType.FindBySubjectName, findValue: subject, validOnly: true);

                if (certificates is [])
                {
                    throw new InvalidOperationException($"Unable to load client certificate with subject \"{subject}\" from key store.");
                }

                return certificates;
            }
        }
    }

    internal sealed class KeyStoreProperties
    {
        public required string Name { get; set; }
        public required StoreLocation Location { get; set; }
    }

    // For testing purposes
    internal int OutgoingResourceSubscriberCount => _outgoingResourceChannels.Count;
    internal int OutgoingInteractionSubscriberCount => _outgoingInteractionChannels.Count;
    internal void SetDashboardServiceClient(Aspire.DashboardService.Proto.V1.DashboardService.DashboardServiceClient client) => _client = client;
    internal Task ResourceWatchCompleteTask => _resourceWatchCompleteTcs.Task;
    internal Task InteractionWatchCompleteTask => _interactionWatchCompleteTcs.Task;

    public bool IsEnabled => _state is not StateDisabled;

    public DashboardConnectionState ConnectionState => _connectionState;

    public event Action<DashboardConnectionState>? ConnectionStateChanged;

    public Task ReconnectAsync()
    {
        if (_state is StateDisabled or StateDisposed)
        {
            return Task.CompletedTask;
        }

        // Cancel any existing reconnect delay to attempt immediately.
        lock (_reconnectDelayLock)
        {
            if (_reconnectDelayCts is { } cts)
            {
                cts.Cancel();
                _reconnectDelayCts = null;
            }
        }

        return Task.CompletedTask;
    }

    private void SetConnectionState(DashboardConnectionState state)
    {
        // Lock ensures that concurrent calls from both watch tasks don't duplicate
        // state transitions or fire the ConnectionStateChanged event multiple times.
        lock (_connectionStateLock)
        {
            if (_connectionState == state)
            {
                return;
            }

            _connectionState = state;
            _logger.LogDebug("Dashboard connection state changed to {State}.", state);

            if (state is DashboardConnectionState.Connected)
            {
                // Complete the WhenConnected TCS so that callers waiting on it can proceed.
                // This handles both initial connection and reconnection after a disconnect.
                _whenConnectedTcs.TrySetResult();
            }
            else if (state is DashboardConnectionState.Disconnected or DashboardConnectionState.Connecting or DashboardConnectionState.Unsupported)
            {
                // Reset the WhenConnected TCS when disconnecting so that callers can re-await it.
                if (_whenConnectedTcs.Task.IsCompleted)
                {
                    _whenConnectedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }

                if (_initialDataReceivedTcs.Task.IsCompleted)
                {
                    _initialDataReceivedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }
            }
        }

        // Invoke the event outside the lock to avoid potential deadlocks
        // if a subscriber tries to access DashboardClient state.
        ConnectionStateChanged?.Invoke(state);
    }

    private void EnsureInitialized()
    {
        var priorState = Interlocked.CompareExchange(ref _state, value: StateInitialized, comparand: StateNone);

        if (priorState is StateDisabled)
        {
            throw new InvalidOperationException($"{nameof(DashboardClient)} is disabled. Check the {nameof(IsEnabled)} property before calling this.");
        }

        if (priorState is not StateNone)
        {
            ObjectDisposedException.ThrowIf(priorState is StateDisposed, this);
            return;
        }

        SetConnectionState(DashboardConnectionState.Connecting);
        // The connection watches resources for the lifetime of the dashboard. Don't let the request or
        // component that first accesses the client become the parent of that long-running operation.
        using (ExecutionContext.SuppressFlow())
        {
            _connection = Task.Run(() => ConnectAndWatchAsync(_clientCancellationToken), _clientCancellationToken);
        }
    }

    async Task ConnectAndWatchAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!await ConnectWithRetryAsync(cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            await Task.WhenAll(
                Task.Run(async () =>
                {
                    await WatchWithRecoveryAsync(WatchResourcesAsync, "resources", cancellationToken).ConfigureAwait(false);
                    _resourceWatchCompleteTcs.TrySetResult();
                }, cancellationToken),
                Task.Run(async () =>
                {
                    await WatchWithRecoveryAsync(WatchInteractionsAsync, "interactions", cancellationToken).ConfigureAwait(false);
                    _interactionWatchCompleteTcs.TrySetResult();
                }, cancellationToken)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Ignore. This is likely caused by the dashboard client being disposed. We don't want to log.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading data from the resource service. For troubleshooting, see {TroubleshootingUrl}", TroubleshootingUrl);
            throw;
        }
    }

    /// <summary>
    /// Attempts to connect to the resource service with exponential backoff retry.
    /// On failure, transitions to Disconnected and waits before retrying. The delay can be
    /// cancelled by <see cref="ReconnectAsync"/> for immediate retry.
    /// </summary>
    private async Task<bool> ConnectWithRetryAsync(CancellationToken cancellationToken)
    {
        var errorCount = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (errorCount > 0)
            {
                SetConnectionState(DashboardConnectionState.Disconnected);

                var delay = TimeSpan.FromSeconds(Math.Min(Math.Pow(2, errorCount - 1), 15));
                _logger.LogDebug("Waiting {Delay} before next connection attempt.", delay);

                // Allow the delay to be cancelled by ReconnectAsync() for immediate retry.
                CancellationTokenSource delayCts;
                lock (_reconnectDelayLock)
                {
                    delayCts = _reconnectDelayCts ??= CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                }
                try
                {
                    await Task.Delay(delay, delayCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // ReconnectAsync() cancelled the delay — retry immediately.
                    _logger.LogDebug("Reconnect delay cancelled, retrying immediately.");
                }
                finally
                {
                    lock (_reconnectDelayLock)
                    {
                        if (ReferenceEquals(_reconnectDelayCts, delayCts))
                        {
                            _reconnectDelayCts = null;
                        }
                    }

                    delayCts.Dispose();
                }

                SetConnectionState(DashboardConnectionState.Connecting);
            }

            try
            {
                var request = new ApplicationInformationRequest();
                var response = await _client!.GetApplicationInformationAsync(request, headers: _headers, cancellationToken: cancellationToken);

                _applicationName = response.ApplicationName;
                _minRequiredVersion = string.IsNullOrEmpty(response.MinDashboardVersion) ? null : response.MinDashboardVersion;

                // MinDashboardVersion is empty when the server predates this field or hasn't set it,
                // which means the dashboard is always considered supported.
                if (!IsDashboardVersionSufficient(s_dashboardVersion, _minRequiredVersion))
                {
                    SetConnectionState(DashboardConnectionState.Unsupported);
                    return false;
                }

                SetConnectionState(DashboardConnectionState.Connected);
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                errorCount++;
                _logger.LogError(ex, "Error #{ErrorCount} connecting to the resource service. For troubleshooting, see {TroubleshootingUrl}", errorCount, TroubleshootingUrl);
            }
        }
    }

    private sealed class RetryContext
    {
        public int ErrorCount { get; set; }
    }

    private async Task WatchWithRecoveryAsync(Func<RetryContext, CancellationToken, Task<RetryResult>> action, string actionName, CancellationToken cancellationToken)
    {
        // Track the number of errors we've seen since the last successfully received message.
        // As this number climbs, we extend the amount of time between reconnection attempts, in
        // order to avoid flooding the server with requests. This value is reset to zero whenever
        // a message is successfully received.
        var retryContext = new RetryContext();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (retryContext.ErrorCount > 0)
            {
                // Transition to disconnected when watch streams fail.
                // Only the first watcher to fail will trigger the state change.
                SetConnectionState(DashboardConnectionState.Disconnected);

                // The most recent attempt failed. There may be more than one failure.
                // We wait for a period of time determined by the number of errors,
                // where the time grows exponentially, until a threshold.
                var delay = ExponentialBackOff(retryContext.ErrorCount, maxSeconds: 15);

                // Allow the delay to be cancelled by ReconnectAsync() for immediate retry.
                // Multiple watchers share the same CTS so ReconnectAsync cancels all pending delays.
                CancellationTokenSource delayCts;
                lock (_reconnectDelayLock)
                {
                    delayCts = _reconnectDelayCts ??= CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                }
                try
                {
                    await Task.Delay(delay, delayCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // ReconnectAsync() cancelled the delay — retry immediately.
                }
                finally
                {
                    lock (_reconnectDelayLock)
                    {
                        // Clear the shared field if we're still the owner, so the next retry
                        // iteration creates a fresh CTS.
                        if (ReferenceEquals(_reconnectDelayCts, delayCts))
                        {
                            _reconnectDelayCts = null;
                        }
                    }

                    // Always dispose locally. CTS.Dispose is idempotent so multiple watchers
                    // or ReconnectAsync disposing the same instance is safe.
                    delayCts.Dispose();
                }

                // Transition to Connecting so that SetConnectionState fires a new Disconnected
                // event on the next failure. Without this, duplicate Disconnected transitions
                // are suppressed and the retry button in ResourceServiceConnectionProvider
                // never appears (it requires multiple disconnect events).
                SetConnectionState(DashboardConnectionState.Connecting);
            }

            try
            {
                if (await action(retryContext, cancellationToken).ConfigureAwait(false) == RetryResult.DoNotRetry)
                {
                    return;
                }
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                // There's a race condition between reconnect attempts and client disposal.
                // This has been observed in unit tests where the client is created and disposed
                // very quickly. This check should probably be in the gRPC library instead.
            }
            catch (RpcException ex)
            {
                retryContext.ErrorCount++;

                _logger.LogError(ex, "Error #{ErrorCount} watching {WatchName}. For troubleshooting, see {TroubleshootingUrl}", retryContext.ErrorCount, actionName, TroubleshootingUrl);
            }
        }

        static TimeSpan ExponentialBackOff(int errorCount, double maxSeconds)
        {
            return TimeSpan.FromSeconds(Math.Min(Math.Pow(2, errorCount - 1), maxSeconds));
        }
    }

    private int CalculateReplicaIndex(string displayName)
    {
        Debug.Assert(Monitor.IsEntered(_lock), "Caller must hold _lock.");

        // There is no consistent way to know which replica is instance 1 vs instance 2. It shouldn't ever matter.
        // This index provides an easy way to identify resources across app runs that takes into account replicas.
        var replicas = _resourceByName.Values.Count(r => r.DisplayName == displayName);
        return replicas + 1;
    }

    private async Task<RetryResult> WatchResourcesAsync(RetryContext retryContext, CancellationToken cancellationToken)
    {
        var call = _client!.WatchResources(new WatchResourcesRequest { IsReconnect = retryContext.ErrorCount != 0 }, headers: _headers, cancellationToken: cancellationToken);

        await foreach (var response in call.ResponseStream.ReadAllAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            using var activity = _activitySource.StartActivity("Process resource update", ActivityKind.Consumer);
            activity?.SetTag("aspire.dashboard.resource_update.type", response.KindCase.ToString());

            List<ResourceViewModelChange>? changes = null;
            var shouldUpdateConnectionState = false;

            lock (_lock)
            {
                // We received a message, which means we are connected. Clear the error count.
                if (retryContext.ErrorCount > 0)
                {
                    retryContext.ErrorCount = 0;
                    shouldUpdateConnectionState = true;
                }

                if (response.KindCase == WatchResourcesUpdate.KindOneofCase.InitialData)
                {
                    var resourcesWithLoadedConsoleLogs = _resourceByName.Values
                        .Where(resource => resource.ConsoleLogsLoaded)
                        .Select(resource => resource.Name)
                        .ToHashSet(StringComparers.ResourceName);

                    // Populate our map using the initial data.
                    _resourceByName.Clear();

                    // TODO send a "clear" event via outgoing channels, in case consumers have extra items to be removed

                    foreach (var resource in response.InitialData.Resources)
                    {
                        // Add to map.
                        var viewModel = resource.ToViewModel(CalculateReplicaIndex(resource.DisplayName), _knownPropertyLookup, _logger);
                        viewModel.ConsoleLogsLoaded = resourcesWithLoadedConsoleLogs.Contains(resource.Name);
                        _resourceByName[resource.Name] = viewModel;

                        // Send this update to any subscribers too.
                        changes ??= [];
                        changes.Add(new(ResourceViewModelChangeType.Upsert, viewModel));
                    }

                    _initialDataReceivedTcs.TrySetResult();
                }
                else if (response.KindCase == WatchResourcesUpdate.KindOneofCase.Changes)
                {
                    // Apply changes to the model.
                    foreach (var change in response.Changes.Value)
                    {
                        changes ??= [];

                        if (change.KindCase == WatchResourcesChange.KindOneofCase.Upsert)
                        {
                            // Upsert (i.e. add or replace)
                            var viewModel = change.Upsert.ToViewModel(CalculateReplicaIndex(change.Upsert.DisplayName), _knownPropertyLookup, _logger);
                            if (_resourceByName.TryGetValue(change.Upsert.Name, out var existingResource))
                            {
                                viewModel.ConsoleLogsLoaded = existingResource.ConsoleLogsLoaded;
                            }
                            _resourceByName[change.Upsert.Name] = viewModel;
                            changes.Add(new(ResourceViewModelChangeType.Upsert, viewModel));
                        }
                        else if (change.KindCase == WatchResourcesChange.KindOneofCase.Delete)
                        {
                            // Remove
                            if (_resourceByName.Remove(change.Delete.ResourceName, out var removed))
                            {
                                changes.Add(new(ResourceViewModelChangeType.Delete, removed));
                            }
                            else
                            {
                                Debug.Fail("Attempt to remove an unknown resource view model.");
                            }
                        }
                        else
                        {
                            throw new FormatException($"Unexpected {nameof(WatchResourcesChange)} kind: {change.KindCase}");
                        }
                    }
                }
                else
                {
                    throw new FormatException($"Unexpected {nameof(WatchResourcesUpdate)} kind: {response.KindCase}");
                }

                // Resolve resource colors for all resources so that color assignment is
                // deterministic of order returned from the service, not order that the color for a resource is first used.
                if (changes is not null)
                {
                    var resolvedNames = _resourceByName.Values
                        .Select(r => ResourceViewModel.GetResourceName(r, _resourceByName));
                    ColorGenerator.Instance.ResolveAll(resolvedNames);
                }
            }

            if (response.KindCase == WatchResourcesUpdate.KindOneofCase.InitialData)
            {
                _resourceRepositoryWriter?.ReplaceResources(response.InitialData.Resources);
            }
            else if (response.KindCase == WatchResourcesUpdate.KindOneofCase.Changes)
            {
                _resourceRepositoryWriter?.ApplyChanges(response.Changes.Value);
            }

            // Update connection state outside the lock to avoid potential deadlocks
            // if a subscriber tries to access DashboardClient state.
            if (shouldUpdateConnectionState)
            {
                SetConnectionState(DashboardConnectionState.Connected);
            }

            if (changes is not null)
            {
                foreach (var channel in _outgoingResourceChannels)
                {
                    // Channel is unbound so TryWrite always succeeds.
                    channel.Writer.TryWrite(changes);
                }
            }
        }

        return RetryResult.Retry;
    }

    private async Task<RetryResult> WatchInteractionsAsync(RetryContext retryContext, CancellationToken cancellationToken)
    {
        // Create the watch interactions call. This is a bidirectional streaming call.
        // Responses are streamed out to all watchers. Requests are sent from the incoming interaction channel.
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var call = _client!.WatchInteractions(headers: _headers, cancellationToken: cts.Token);

        if (await IsUnimplemented(call).ConfigureAwait(false))
        {
            // The server does not support this method.
            _logger.LogWarning("Server does not support interactions.");

            return RetryResult.DoNotRetry;
        }

        // Send
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var update in _incomingInteractionChannel.Reader.ReadAllAsync(cts.Token).ConfigureAwait(false))
                {
                    await call.RequestStream.WriteAsync(update).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error writing to interaction request stream.");
            }
            finally
            {
                // Cancel the call if we can't write to it.
                // Most likely reading from the response stream has already failed but force cancellation and the interaction call is retry just in case.
                cts.Cancel();
            }
        }, cts.Token);

        // Receive
        try
        {
            await foreach (var response in call.ResponseStream.ReadAllAsync(cancellationToken: cts.Token).ConfigureAwait(false))
            {
                // We received a message, which means we are connected. Clear the error count.
                if (retryContext.ErrorCount > 0)
                {
                    retryContext.ErrorCount = 0;
                    SetConnectionState(DashboardConnectionState.Connected);
                }

                lock (_lock)
                {
                    if (response.Complete != null)
                    {
                        // Interaction finished. Remove from pending collection.
                        _pendingInteractionCollection.Remove(response.InteractionId);
                    }
                    else
                    {
                        if (_pendingInteractionCollection.Contains(response.InteractionId))
                        {
                            _pendingInteractionCollection.Remove(response.InteractionId);
                        }
                        _pendingInteractionCollection.Add(response);
                    }
                }

                foreach (var channel in _outgoingInteractionChannels)
                {
                    // Channel is unbound so TryWrite always succeeds.
                    channel.Writer.TryWrite(response);
                }
            }
        }
        finally
        {
            // Ensure the write task is cancelled if we exit the loop.
            cts.Cancel();
        }

        return RetryResult.Retry;
    }

    private static async Task<bool> IsUnimplemented(AsyncDuplexStreamingCall<WatchInteractionsRequestUpdate, WatchInteractionsResponseUpdate> call)
    {
        // Wait for the server to respond with initial headers. Require before calling GetStatus.
        await call.ResponseHeadersAsync.ConfigureAwait(false);

        try
        {
            var status = call.GetStatus();
            if (status.StatusCode == StatusCode.Unimplemented)
            {
                return true;
            }
        }
        catch (InvalidOperationException)
        {
            // Expected from GetStatus when the method is still in progress.
        }

        return false;
    }

    public async Task SendInteractionRequestAsync(WatchInteractionsRequestUpdate request, CancellationToken cancellationToken)
    {
        await _incomingInteractionChannel.Writer.WriteAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public Task WhenConnected
    {
        get
        {
            // All pages wait for this task (it is used to display the title) but some don't subscribe to resources.
            // If someone is waiting for the connection, we need to ensure connection is starting.
            EnsureInitialized();

            return _whenConnectedTcs.Task;
        }
    }

    public string ApplicationName
    {
        get => _applicationName
            ?? _dashboardOptions.ApplicationName
            ?? "Aspire";
    }

    public string? MinRequiredVersion => _minRequiredVersion;

    public ResourceViewModel? GetResource(string resourceName)
    {
        EnsureInitialized();
        lock (_lock)
        {
            if (_resourceByName.TryGetValue(resourceName, out var resource))
            {
                return resource;
            }
            return null;
        }
    }

    public IReadOnlyList<ResourceViewModel> GetResources()
    {
        EnsureInitialized();
        lock (_lock)
        {
            return _resourceByName.Values.ToList();
        }
    }

    public async Task<ResourceViewModelSubscription> SubscribeResourcesAsync(CancellationToken cancellationToken)
    {
        EnsureInitialized();

        var cts = CancellationTokenSource.CreateLinkedTokenSource(_clientCancellationToken, cancellationToken);

        // Wait for initial data to be received from the server. This allows initial data to be returned with subscription when client is starting.
        await _initialDataReceivedTcs.Task.WaitAsync(cts.Token).ConfigureAwait(false);

        // There are two types of channel in this class. This is not a gRPC channel.
        // It's a producer-consumer queue channel, used to push updates to subscribers
        // without blocking the producer here.
        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>(
            new UnboundedChannelOptions { AllowSynchronousContinuations = false, SingleReader = true, SingleWriter = true });

        lock (_lock)
        {
            ImmutableInterlocked.Update(ref _outgoingResourceChannels, static (set, channel) => set.Add(channel), channel);

            return new ResourceViewModelSubscription(
                InitialState: _resourceByName.Values.ToImmutableArray(),
                Subscription: StreamUpdatesAsync(cts.Token));
        }

        async IAsyncEnumerable<IReadOnlyList<ResourceViewModelChange>> StreamUpdatesAsync([EnumeratorCancellation] CancellationToken enumeratorCancellationToken = default)
        {
            try
            {
                await foreach (var batch in channel.GetBatchesAsync(minReadInterval: TimeSpan.FromMilliseconds(100), cancellationToken: enumeratorCancellationToken).ConfigureAwait(false))
                {
                    if (batch.Count == 1)
                    {
                        yield return batch[0];
                    }
                    else
                    {
                        yield return batch.SelectMany(batch => batch).ToList();
                    }
                }
            }
            finally
            {
                cts.Dispose();
                ImmutableInterlocked.Update(ref _outgoingResourceChannels, static (set, channel) => set.Remove(channel), channel);
            }
        }
    }

    public IAsyncEnumerable<WatchInteractionsResponseUpdate> SubscribeInteractionsAsync(CancellationToken cancellationToken)
    {
        EnsureInitialized();

        var cts = CancellationTokenSource.CreateLinkedTokenSource(_clientCancellationToken, cancellationToken);

        // There are two types of channel in this class. This is not a gRPC channel.
        // It's a producer-consumer queue channel, used to push updates to subscribers
        // without blocking the producer here.
        var channel = Channel.CreateUnbounded<WatchInteractionsResponseUpdate>(
            new UnboundedChannelOptions { AllowSynchronousContinuations = false, SingleReader = true, SingleWriter = true });

        lock (_lock)
        {
            ImmutableInterlocked.Update(ref _outgoingInteractionChannels, static (set, channel) => set.Add(channel), channel);

            return StreamUpdatesAsync(_pendingInteractionCollection.ToList(), cts.Token);
        }

        async IAsyncEnumerable<WatchInteractionsResponseUpdate> StreamUpdatesAsync(List<WatchInteractionsResponseUpdate> pendingInteractions, [EnumeratorCancellation] CancellationToken enumeratorCancellationToken = default)
        {
            try
            {
                foreach (var item in pendingInteractions)
                {
                    yield return item;
                }

                await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken: enumeratorCancellationToken).ConfigureAwait(false))
                {
                    yield return item;
                }
            }
            finally
            {
                cts.Dispose();
                ImmutableInterlocked.Update(ref _outgoingInteractionChannels, static (set, channel) => set.Remove(channel), channel);
            }
        }
    }

    public async IAsyncEnumerable<IReadOnlyList<ResourceLogLine>> SubscribeConsoleLogs(string resourceName, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        EnsureInitialized();

        // Console-log persistence is demand-driven rather than always-on. This known limitation means
        // historical runs can omit logs for resources that were never viewed or exported. The historical
        // Console Logs page checks this capture state and displays a notice when logs aren't available.
        // See https://github.com/microsoft/aspire/issues/18823.
        MarkConsoleLogsLoaded(resourceName);

        // It's ok to dispose CTS with using because this method exits after it is finished being used.
        using var combinedTokens = CancellationTokenSource.CreateLinkedTokenSource(_clientCancellationToken, cancellationToken);

        var call = _client!.WatchResourceConsoleLogs(
            new WatchResourceConsoleLogsRequest() { ResourceName = resourceName },
            headers: _headers,
            cancellationToken: combinedTokens.Token);

        // Write incoming logs to a channel, and then read from that channel to yield the logs.
        // We do this to batch logs together and enforce a minimum read interval.
        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceLogLine>>(
            new UnboundedChannelOptions { AllowSynchronousContinuations = false, SingleReader = true, SingleWriter = true });

        var readTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var response in call.ResponseStream.ReadAllAsync(cancellationToken: combinedTokens.Token).ConfigureAwait(false))
                {
                    _resourceRepositoryWriter?.AddConsoleLogs(resourceName, response.LogLines);
                    // Channel is unbound so TryWrite always succeeds.
                    channel.Writer.TryWrite(CreateLogLines(response.LogLines));
                }
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        }, combinedTokens.Token);

        await foreach (var batch in channel.Reader.ReadAllAsync(combinedTokens.Token).ConfigureAwait(false))
        {
            yield return batch;
        }

        await readTask.ConfigureAwait(false);
    }

    public async IAsyncEnumerable<IReadOnlyList<ResourceLogLine>> GetConsoleLogs(string resourceName, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        EnsureInitialized();

        MarkConsoleLogsLoaded(resourceName);

        using var combinedTokens = CancellationTokenSource.CreateLinkedTokenSource(_clientCancellationToken, cancellationToken);

        var call = _client!.WatchResourceConsoleLogs(
            new WatchResourceConsoleLogsRequest() { ResourceName = resourceName, SuppressFollow = true },
            headers: _headers,
            cancellationToken: combinedTokens.Token);

        await foreach (var response in call.ResponseStream.ReadAllAsync(cancellationToken: combinedTokens.Token).ConfigureAwait(false))
        {
            _resourceRepositoryWriter?.AddConsoleLogs(resourceName, response.LogLines);
            yield return CreateLogLines(response.LogLines);
        }
    }

    private void MarkConsoleLogsLoaded(string resourceName)
    {
        lock (_lock)
        {
            if (_resourceByName.TryGetValue(resourceName, out var resource))
            {
                resource.ConsoleLogsLoaded = true;
            }
        }

        _resourceRepositoryWriter?.MarkConsoleLogsLoaded(resourceName);
    }

    private static ResourceLogLine[] CreateLogLines(IList<ConsoleLogLine> logLines)
    {
        var resourceLogLines = new ResourceLogLine[logLines.Count];

        for (var i = 0; i < logLines.Count; i++)
        {
            resourceLogLines[i] = new ResourceLogLine(logLines[i].LineNumber, logLines[i].Text, logLines[i].IsStdErr);
        }

        return resourceLogLines;
    }

    public async Task<ResourceCommandResponseViewModel> ExecuteResourceCommandAsync(string resourceName, string resourceType, CommandViewModel command, ExecuteResourceCommandOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        EnsureInitialized();

        var request = new ResourceCommandRequest()
        {
            CommandName = command.Name,
            ResourceName = resourceName,
            ResourceType = resourceType,
            NonInteractive = options.NonInteractive
        };

        if (options.Arguments is { } arguments)
        {
            foreach (var (key, value) in arguments)
            {
                request.Arguments.Add(key, value);
            }
        }

        try
        {
            using var combinedTokens = CancellationTokenSource.CreateLinkedTokenSource(_clientCancellationToken, cancellationToken);

            var response = await _client!.ExecuteResourceCommandAsync(request, headers: _headers, cancellationToken: combinedTokens.Token);

            return response.ToViewModel();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new ResourceCommandResponseViewModel()
            {
                Kind = ResourceCommandResponseKind.Cancelled
            };
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled && cancellationToken.IsCancellationRequested)
        {
            return new ResourceCommandResponseViewModel()
            {
                Kind = ResourceCommandResponseKind.Cancelled
            };
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled && _clientCancellationToken.IsCancellationRequested)
        {
            var errorMessage = _loc[nameof(DashboardResources.ResourceCommandAppHostDisconnected)];

            return new ResourceCommandResponseViewModel()
            {
                Kind = ResourceCommandResponseKind.Failed,
                ErrorMessage = errorMessage,
                Message = errorMessage
            };
        }
        catch (RpcException ex)
        {
            _logger.LogError(ex, "Error executing command \"{CommandName}\" on resource \"{ResourceName}\": {StatusCode}", command.Name, resourceName, ex.StatusCode);

            var errorMessage = ex.StatusCode switch
            {
                StatusCode.Unimplemented => "Command not implemented",
                StatusCode.Unavailable => _loc[nameof(DashboardResources.ResourceCommandAppHostDisconnected)],
                _ => "Unknown error. See logs for details"
            };

            return new ResourceCommandResponseViewModel()
            {
                Kind = ResourceCommandResponseKind.Failed,
                ErrorMessage = errorMessage,
                Message = errorMessage
            };
        }
    }

    public async Task<string> UploadFileAsync(Stream fileStream, string fileName, long expectedSize, CancellationToken cancellationToken)
    {
        EnsureInitialized();

        using var combinedTokens = CancellationTokenSource.CreateLinkedTokenSource(_clientCancellationToken, cancellationToken);
        using var call = _client!.UploadFile(headers: _headers, cancellationToken: combinedTokens.Token);

        const int chunkSize = 64 * 1024; // 64 KB chunks
        var buffer = new byte[chunkSize];
        var isFirst = true;
        long totalBytesRead = 0;

        int bytesRead;
        while ((bytesRead = await fileStream.ReadAsync(buffer, combinedTokens.Token).ConfigureAwait(false)) > 0)
        {
            totalBytesRead += bytesRead;
            if (totalBytesRead > expectedSize)
            {
                throw new InvalidOperationException($"File '{fileName}' exceeded the expected size of {expectedSize} bytes.");
            }

            var chunk = new UploadFileChunk
            {
                Data = Google.Protobuf.ByteString.CopyFrom(buffer, 0, bytesRead)
            };

            if (isFirst)
            {
                chunk.FileName = fileName;
            }

            await call.RequestStream.WriteAsync(chunk, combinedTokens.Token).ConfigureAwait(false);
            isFirst = false;
        }

        // Handle case where the file was empty — still send filename.
        if (isFirst)
        {
            await call.RequestStream.WriteAsync(new UploadFileChunk { FileName = fileName }, combinedTokens.Token).ConfigureAwait(false);
        }

        await call.RequestStream.CompleteAsync().ConfigureAwait(false);

        var response = await call.ResponseAsync.ConfigureAwait(false);
        return response.FileId;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _state, StateDisposed) is not StateDisposed)
        {
            _outgoingResourceChannels = [];
            _outgoingInteractionChannels = [];

            _cts.Cancel();
            _cts.Dispose();

            _channel?.Dispose();

            await TaskHelpers.WaitIgnoreCancelAsync(_connection, _logger, "Unexpected error from connection task.").ConfigureAwait(false);
        }
    }

    // Internal for testing.
    internal void SetConnectionStateForTesting(DashboardConnectionState state) => SetConnectionState(state);

    // Internal for testing.
    // TODO: Improve this in the future by making the client injected with DI and have it return data.
    internal void SetInitialDataReceived(IList<Resource>? initialData = null)
    {
        if (initialData != null)
        {
            lock (_lock)
            {
                foreach (var data in initialData)
                {
                    _resourceByName[data.Name] = data.ToViewModel(CalculateReplicaIndex(data.DisplayName), _knownPropertyLookup, _logger);
                }
            }
        }

        _initialDataReceivedTcs.TrySetResult();
    }

    private class InteractionCollection : KeyedCollection<int, WatchInteractionsResponseUpdate>
    {
        protected override int GetKeyForItem(WatchInteractionsResponseUpdate item) => item.InteractionId;
    }

    private enum RetryResult
    {
        Retry,
        DoNotRetry
    }

    private static SemVersion? GetDashboardVersion()
    {
        // The informational version contains the full semver string stamped at build time
        // (e.g. "13.5.0-preview.1.26307.2+commitHash").
        var informationalVersion = Shared.AssemblyVersionHelper.GetInformationalVersion(typeof(DashboardClient).Assembly);
        if (informationalVersion is not { Length: > 0 })
        {
            return null;
        }

        return SemVersion.TryParse(informationalVersion, SemVersionStyles.Any, out var version) ? version : null;
    }

    /// <summary>
    /// Compares the dashboard version against the required version, ignoring pre-release labels.
    /// A dashboard version of "13.5.0-dev" is considered sufficient for a requirement of "13.5.0".
    /// Returns <see langword="true"/> when no version requirement is specified or the dashboard meets it.
    /// </summary>
    internal static bool IsDashboardVersionSufficient(SemVersion? dashboardVersion, string? requiredVersionText)
    {
        // No requirement specified — always sufficient.
        if (string.IsNullOrEmpty(requiredVersionText))
        {
            return true;
        }

        // Can't parse the requirement — treat as sufficient to avoid blocking users.
        if (!SemVersion.TryParse(requiredVersionText, SemVersionStyles.Any, out var requiredVersion))
        {
            return true;
        }

        // Dashboard version unknown — can't verify, treat as insufficient.
        if (dashboardVersion is null)
        {
            return false;
        }

        // Strip pre-release from both versions so that dev/preview builds
        // are treated as equivalent to their release counterpart.
        var dashboardRelease = new SemVersion(dashboardVersion.Major, dashboardVersion.Minor, dashboardVersion.Patch);
        var requiredRelease = new SemVersion(requiredVersion.Major, requiredVersion.Minor, requiredVersion.Patch);

        return SemVersion.ComparePrecedence(dashboardRelease, requiredRelease) >= 0;
    }
}
