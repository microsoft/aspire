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
    internal const string LiveAppHostServiceKey = "LiveAppHost";

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
    private readonly IResourceRepositoryWriter _resourceRepositoryWriter;
    private readonly DashboardDataSourcePool? _dataSourcePool;
    private string _appHostRunId = string.Empty;
    private readonly AsyncLock _attachmentLock = new();
    private bool _hasWatchedResources;
    private ImmutableHashSet<string> _appHostCorsOrigins = ImmutableHashSet<string>.Empty;

    internal bool IsAppHostOriginAllowed(string origin) => Volatile.Read(ref _appHostCorsOrigins).Contains(origin);
    private CancellationTokenSource _appHostCts = new();
    private CancellationToken _appHostToken;

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
        IResourceRepositoryWriter resourceRepositoryWriter,
        DashboardDataSourcePool? dataSourcePool,
        Action<SocketsHttpHandler>? configureHttpHandler = null)
    {
        _activitySource = activitySource.ActivitySource;
        _loggerFactory = loggerFactory;
        _knownPropertyLookup = knownPropertyLookup;
        _dashboardOptions = dashboardOptions.Value;
        _loc = loc;
        _resourceRepositoryWriter = resourceRepositoryWriter;
        _dataSourcePool = dataSourcePool;
        _appHostToken = _appHostCts.Token;

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

                BindSslClientAuthenticationOptions(
                    configuration.GetSection("Dashboard:ResourceServiceClient:Ssl"),
                    httpHandler.SslOptions);
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
                    // Transport backoff is independent of RPC retries and defaults to two minutes.
                    // A persistent dashboard must reattach before the AppHost's startup gate expires.
                    MaxReconnectBackoff = TimeSpan.FromSeconds(5),
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

                // Intentionally accept only PKCS#12/PFX files for client authentication. Other
                // formats accepted by the old constructor generally cannot supply a private key;
                // certificate-store loading remains available for those configurations.
                // https://github.com/microsoft/aspire/pull/5688#issuecomment-2350856704
                return [X509CertificateLoader.LoadPkcs12FromFile(filePath, password)];
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

    internal static void BindSslClientAuthenticationOptions(
        IConfigurationSection configuration,
        SslClientAuthenticationOptions options)
    {
        // SslClientAuthenticationOptions exposes certificate collections and callback properties
        // that the configuration binding generator cannot construct. Preserve the supported scalar
        // overrides without falling back to reflection under Native AOT.
        options.AllowRenegotiation = configuration.GetValue(
            nameof(SslClientAuthenticationOptions.AllowRenegotiation),
            options.AllowRenegotiation);
        options.AllowTlsResume = configuration.GetValue(
            nameof(SslClientAuthenticationOptions.AllowTlsResume),
            options.AllowTlsResume);
        options.CertificateRevocationCheckMode = configuration.GetValue(
            nameof(SslClientAuthenticationOptions.CertificateRevocationCheckMode),
            options.CertificateRevocationCheckMode);
        options.EnabledSslProtocols = configuration.GetValue(
            nameof(SslClientAuthenticationOptions.EnabledSslProtocols),
            options.EnabledSslProtocols);
        options.EncryptionPolicy = configuration.GetValue(
            nameof(SslClientAuthenticationOptions.EncryptionPolicy),
            options.EncryptionPolicy);
        options.TargetHost = configuration.GetValue(
            nameof(SslClientAuthenticationOptions.TargetHost),
            options.TargetHost);
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

    private void SetAppHostCorsOrigins(string origins) =>
        Volatile.Write(ref _appHostCorsOrigins, origins
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            // AppHost metadata can contain "https://localhost:443" while the browser sends
            // Origin: https://localhost. Preserve paths/user info so malformed origins don't
            // accidentally grant access to their host when removing a default port.
            // https://www.rfc-editor.org/rfc/rfc6454#section-6.2
            .Select(static origin => Uri.TryCreate(origin, UriKind.Absolute, out var uri) &&
                (uri.Scheme is "http" or "https") && uri.IsDefaultPort &&
                origin.EndsWith($":{uri.Port}", StringComparison.Ordinal)
                    ? origin[..origin.LastIndexOf(':')]
                    : origin)
            .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase));

    public string AppHostRunId
    {
        get
        {
            lock (_lock)
            {
                return _appHostRunId;
            }
        }
    }

    private Metadata GetSessionHeaders() => GetSessionHeaders(_appHostRunId);

    private Metadata GetSessionHeaders(string runId)
    {
        var headers = new Metadata();
        foreach (var entry in _headers)
        {
            headers.Add(entry);
        }
        if (!string.IsNullOrEmpty(runId))
        {
            headers.Add("x-aspire-apphost-run-id", runId);
        }
        return headers;
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
                    await WatchWithRecoveryAsync(WatchResourcesAsync, "resources", onRetry: ResetResourceReadiness, updateConnectionState: true, cancellationToken).ConfigureAwait(false);
                    _resourceWatchCompleteTcs.TrySetResult();
                }, cancellationToken),
                Task.Run(async () =>
                {
                    await WatchWithRecoveryAsync(WatchInteractionsAsync, "interactions", onRetry: null, updateConnectionState: false, cancellationToken).ConfigureAwait(false);
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

                using (await _attachmentLock.LockAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (_appHostRunId != response.RunId)
                    {
                        _appHostCts.Cancel();
                        _appHostCts.Dispose();
                        _appHostCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        _appHostToken = _appHostCts.Token;
                        if (_dataSourcePool is not null)
                        {
                            await _dataSourcePool.StartAppHostRunAsync(response.RunId, cancellationToken).ConfigureAwait(false);
                        }
                        lock (_lock)
                        {
                            foreach (var interaction in _pendingInteractionCollection)
                            {
                                var complete = new WatchInteractionsResponseUpdate { InteractionId = interaction.InteractionId, AppHostRunId = _appHostRunId, Complete = new InteractionComplete() };
                                foreach (var channel in _outgoingInteractionChannels)
                                {
                                    channel.Writer.TryWrite(complete);
                                }
                            }
                            foreach (var resource in _resourceByName.Values)
                            {
                                resource.ConsoleLogsLoaded = false;
                            }
                            _pendingInteractionCollection.Clear();
                            while (_incomingInteractionChannel.Reader.TryRead(out _))
                            {
                            }
                            _appHostRunId = response.RunId;
                        }
                    }
                    var consoleWriter = (_dataSourcePool?.Current.ResourceRepository as IAppHostConsoleLogWriter)
                        ?? (_resourceRepositoryWriter as IAppHostConsoleLogWriter);
                    if (consoleWriter is not null)
                    {
                        await consoleWriter.BeginAppHostRunAsync(response.RunId).ConfigureAwait(false);
                    }
                }

                SetAppHostCorsOrigins(response.CorsAllowedOrigins);
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

    private void ResetResourceReadiness()
    {
        lock (_lock)
        {
            // Only a resource-stream restart requires another snapshot. Interaction failures don't invalidate
            // resource data, and retries before a snapshot must retain the task existing callers are awaiting.
            if (_initialDataReceivedTcs.Task.IsCompleted)
            {
                _initialDataReceivedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
    }

    private async Task WatchWithRecoveryAsync(Func<RetryContext, CancellationToken, Task<RetryResult>> action, string actionName, Action? onRetry, bool updateConnectionState, CancellationToken cancellationToken)
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
                // Resource snapshots define dashboard readiness. Interaction streams can be idle after
                // reconnecting, so their independent retries must not leave resource pages waiting forever.
                if (updateConnectionState)
                {
                    SetConnectionState(DashboardConnectionState.Disconnected);
                }

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
                if (updateConnectionState)
                {
                    SetConnectionState(DashboardConnectionState.Connecting);
                }
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
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                retryContext.ErrorCount++;
            }
            catch (RpcException ex)
            {
                retryContext.ErrorCount++;

                _logger.LogError(ex, "Error #{ErrorCount} watching {WatchName}. For troubleshooting, see {TroubleshootingUrl}", retryContext.ErrorCount, actionName, TroubleshootingUrl);
            }

            onRetry?.Invoke();
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
        if (_hasWatchedResources)
        {
            if (!await ConnectWithRetryAsync(cancellationToken).ConfigureAwait(false))
            {
                return RetryResult.DoNotRetry;
            }
        }
        _hasWatchedResources = true;

        var call = _client!.WatchResources(new WatchResourcesRequest { IsReconnect = retryContext.ErrorCount != 0, RunId = _appHostRunId }, headers: GetSessionHeaders(), cancellationToken: cancellationToken);

        await foreach (var response in call.ResponseStream.ReadAllAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            if (response.HasCorsAllowedOrigins)
            {
                // Endpoint allocation publishes resource URL updates after the AppHost handshake.
                // Refresh origins here so a surviving dashboard accepts browser OTLP from new ports.
                SetAppHostCorsOrigins(response.CorsAllowedOrigins);
            }
            using var activity = _activitySource.StartActivity("Process resource update", ActivityKind.Consumer);
            activity?.SetTag("aspire.dashboard.resource_update.type", response.KindCase.ToString());

            List<ResourceViewModelChange>? changes = null;
            ImmutableHashSet<Channel<IReadOnlyList<ResourceViewModelChange>>> resourceChannels = [];
            var shouldUpdateConnectionState = false;
            var initialDataReceivedTcs = _initialDataReceivedTcs;

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

                    // A new snapshot can remove resources, including after an AppHost run change.
                    // Notify long-lived consumers before replacing the in-memory map.
                    var replacementNames = response.InitialData.Resources.Select(r => r.Name).ToHashSet(StringComparers.ResourceName);
                    foreach (var resource in _resourceByName.Values.Where(r => !replacementNames.Contains(r.Name)))
                    {
                        changes ??= [];
                        changes.Add(new(ResourceViewModelChangeType.Delete, resource));
                    }
                    _resourceByName.Clear();

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

                    // Capture subscribers atomically with the model transition. A subscriber added after this
                    // point receives the updated model in its initial snapshot and must not also receive this change.
                    resourceChannels = _outgoingResourceChannels;
                }
            }

            if (response.KindCase == WatchResourcesUpdate.KindOneofCase.InitialData)
            {
                await _resourceRepositoryWriter.ReplaceResourcesAsync(response.InitialData.Resources).ConfigureAwait(false);
                // SelectedDashboardClient reads the persisted repository, not the in-memory map.
                // Complete this snapshot's readiness only after both contain the initial resources.
                initialDataReceivedTcs.TrySetResult();
            }
            else if (response.KindCase == WatchResourcesUpdate.KindOneofCase.Changes)
            {
                await _resourceRepositoryWriter.ApplyChangesAsync(response.Changes.Value).ConfigureAwait(false);
            }

            // Update connection state outside the lock to avoid potential deadlocks
            // if a subscriber tries to access DashboardClient state.
            if (shouldUpdateConnectionState)
            {
                SetConnectionState(DashboardConnectionState.Connected);
            }

            if (changes is not null)
            {
                foreach (var channel in resourceChannels)
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
        string runId;
        CancellationToken attachmentToken;
        using (await _attachmentLock.LockAsync(cancellationToken).ConfigureAwait(false))
        {
            runId = _appHostRunId;
            attachmentToken = _appHostToken;
        }
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, attachmentToken);
        using var call = _client!.WatchInteractions(headers: GetSessionHeaders(runId), cancellationToken: cts.Token);

        if (await IsUnimplemented(call).ConfigureAwait(false))
        {
            // The server does not support this method.
            _logger.LogWarning("Server does not support interactions.");

            return RetryResult.DoNotRetry;
        }

        // Send
        var sendTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var update in _incomingInteractionChannel.Reader.ReadAllAsync(cts.Token).ConfigureAwait(false))
                {
                    if (update.AppHostRunId == runId)
                    {
                        await call.RequestStream.WriteAsync(update).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
            }
            catch (Exception ex)
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
                }

                lock (_lock)
                {
                    if (_appHostRunId != runId)
                    {
                        continue;
                    }
                    response.AppHostRunId = runId;
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
                    foreach (var channel in _outgoingInteractionChannels)
                    {
                        // Publish before a run transition can enqueue completion of this interaction.
                        channel.Writer.TryWrite(response);
                    }
                }
            }
        }
        finally
        {
            // Ensure the write task is cancelled if we exit the loop.
            cts.Cancel();
            try
            {
                // Finish consuming the old queue before another stream can read replies for a new run.
                await sendTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
            }
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

    public Task SendInteractionRequestAsync(WatchInteractionsRequestUpdate request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            if (request.AppHostRunId == _appHostRunId)
            {
                // The unbounded queue and run transition share this lock. A delayed callback from
                // a retired run cannot enter a stream whose header identifies the new AppHost.
                _incomingInteractionChannel.Writer.TryWrite(request);
            }
        }

        return Task.CompletedTask;
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
        get => string.IsNullOrWhiteSpace(_applicationName)
            ? _dashboardOptions.GetApplicationNameOrDefault()
            : _applicationName;
    }

    public string? MinRequiredVersion => _minRequiredVersion;

    public Task WhenResourcesReady
    {
        get
        {
            EnsureInitialized();

            lock (_lock)
            {
                return _initialDataReceivedTcs.Task;
            }
        }
    }

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
        await WhenResourcesReady.WaitAsync(cts.Token).ConfigureAwait(false);

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

    internal async Task CaptureConsoleLogsAsync(string resourceName, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_clientCancellationToken, cancellationToken);
        while (true)
        {
            await WhenConnected.WaitAsync(cts.Token).ConfigureAwait(false);
            await WhenResourcesReady.WaitAsync(cts.Token).ConfigureAwait(false);
            try
            {
                await foreach (var _ in SubscribeConsoleLogs(resourceName, cts.Token).ConfigureAwait(false))
                {
                    // SubscribeConsoleLogs persists each batch. The selected-run reader receives it from SQLite.
                }
            }
            catch (OperationCanceledException) when (!cts.IsCancellationRequested)
            {
                // A new attachment cancelled the previous RPC; keep the viewed resource subscribed.
            }
            catch (RpcException ex) when (ex.StatusCode is StatusCode.Unavailable or StatusCode.Cancelled or StatusCode.FailedPrecondition or StatusCode.NotFound)
            {
                _logger.LogDebug(ex, "Console log capture for {ResourceName} will resume after reconnecting.", resourceName);
            }

            // A resource can end its stream before connection state changes. Avoid a hot retry loop.
            await Task.Delay(TimeSpan.FromSeconds(1), cts.Token).ConfigureAwait(false);
        }
    }

    public async IAsyncEnumerable<IReadOnlyList<ResourceLogLine>> SubscribeConsoleLogs(string resourceName, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        EnsureInitialized();

        // Console-log persistence is demand-driven rather than always-on. This known limitation means
        // historical runs can omit logs for resources that were never viewed or exported. The historical
        // Console Logs page checks this capture state and displays a notice when logs aren't available.
        // See https://github.com/microsoft/aspire/issues/18823.
        using var attachment = await RetainConsoleLogAttachmentAsync(cancellationToken).ConfigureAwait(false);
        await MarkConsoleLogsLoadedAsync(resourceName, attachment).ConfigureAwait(false);

        // It's ok to dispose CTS with using because this method exits after it is finished being used.
        using var combinedTokens = CancellationTokenSource.CreateLinkedTokenSource(_clientCancellationToken, cancellationToken, attachment.Token);

        using var call = _client!.WatchResourceConsoleLogs(
            new WatchResourceConsoleLogsRequest() { ResourceName = resourceName },
            headers: GetSessionHeaders(attachment.RunId),
            cancellationToken: combinedTokens.Token);

        // Write incoming logs to a channel, and then read from that channel to yield the logs.
        // We do this to batch logs together and enforce a minimum read interval.
        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceLogLine>>(
            new UnboundedChannelOptions { AllowSynchronousContinuations = false, SingleReader = true, SingleWriter = true });

        var readTask = ReadAsync();
        try
        {
            await foreach (var batch in channel.Reader.ReadAllAsync(combinedTokens.Token).ConfigureAwait(false))
            {
                yield return batch;
            }
        }
        finally
        {
            // Stop and drain the reader before releasing its run. A batch already received from the
            // old AppHost must finish writing to that database rather than the replacement run.
            combinedTokens.Cancel();
            await readTask.ConfigureAwait(false);
        }

        async Task ReadAsync()
        {
            try
            {
                await foreach (var response in call.ResponseStream.ReadAllAsync(cancellationToken: combinedTokens.Token).ConfigureAwait(false))
                {
                    if (await AddConsoleLogsAsync(attachment, resourceName, response.LogLines).ConfigureAwait(false))
                    {
                        channel.Writer.TryWrite(CreateLogLines(response.LogLines));
                    }
                }
                channel.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                channel.Writer.TryComplete(ex);
            }
        }
    }

    public async IAsyncEnumerable<IReadOnlyList<ResourceLogLine>> GetConsoleLogs(string resourceName, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        EnsureInitialized();

        using var attachment = await RetainConsoleLogAttachmentAsync(cancellationToken).ConfigureAwait(false);
        await MarkConsoleLogsLoadedAsync(resourceName, attachment).ConfigureAwait(false);

        using var combinedTokens = CancellationTokenSource.CreateLinkedTokenSource(_clientCancellationToken, cancellationToken, attachment.Token);

        using var call = _client!.WatchResourceConsoleLogs(
            new WatchResourceConsoleLogsRequest() { ResourceName = resourceName, SuppressFollow = true },
            headers: GetSessionHeaders(attachment.RunId),
            cancellationToken: combinedTokens.Token);

        await foreach (var response in call.ResponseStream.ReadAllAsync(cancellationToken: combinedTokens.Token).ConfigureAwait(false))
        {
            if (await AddConsoleLogsAsync(attachment, resourceName, response.LogLines).ConfigureAwait(false))
            {
                yield return CreateLogLines(response.LogLines);
            }
        }
    }

    /// <inheritdoc/>
    public Task ClearConsoleLogsAsync(IReadOnlyList<string> resourceNames, DateTime clearDate) =>
        _resourceRepositoryWriter.ClearConsoleLogsAsync(resourceNames, clearDate);

    private async ValueTask<ConsoleLogAttachment> RetainConsoleLogAttachmentAsync(CancellationToken cancellationToken)
    {
        // The first handshake initializes the repository's console generation. Capturing earlier
        // would pair an uninitialized writer with an empty run identity and could drop the first batch.
        using var connectionLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _clientCancellationToken);
        await WhenConnected.WaitAsync(connectionLifetime.Token).ConfigureAwait(false);
        using (await _attachmentLock.LockAsync(connectionLifetime.Token).ConfigureAwait(false))
        {
            // The writer, RPC identity, and cancellation token must describe the same attachment.
            // In particular, MarkConsoleLogsLoadedAsync can await SQLite while a reconnect completes.
            IDisposable? retention = null;
            var writer = _resourceRepositoryWriter;
            if (_dataSourcePool is not null)
            {
                retention = _dataSourcePool.RetainCurrent(out var run, out _);
                writer = (IResourceRepositoryWriter)run.ResourceRepository;
            }
            return new ConsoleLogAttachment(retention, writer, _appHostRunId, _appHostToken);
        }
    }

    private async Task MarkConsoleLogsLoadedAsync(string resourceName, ConsoleLogAttachment attachment)
    {
        lock (_lock)
        {
            if (attachment.RunId == _appHostRunId && _resourceByName.TryGetValue(resourceName, out var resource))
            {
                resource.ConsoleLogsLoaded = true;
            }
        }

        if (attachment.Writer is IAppHostConsoleLogWriter writer)
        {
            await writer.MarkConsoleLogsLoadedAsync(resourceName, attachment.RunId).ConfigureAwait(false);
        }
        else
        {
            await attachment.Writer.MarkConsoleLogsLoadedAsync(resourceName).ConfigureAwait(false);
        }
    }

    private static async Task<bool> AddConsoleLogsAsync(ConsoleLogAttachment attachment, string resourceName, IReadOnlyList<ConsoleLogLine> lines)
    {
        if (attachment.Writer is IAppHostConsoleLogWriter writer)
        {
            return await writer.AddConsoleLogsAsync(resourceName, lines, attachment.RunId).ConfigureAwait(false);
        }

        await attachment.Writer.AddConsoleLogsAsync(resourceName, lines).ConfigureAwait(false);
        return true;
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

        string runId;
        CancellationToken attachmentToken;
        using (await _attachmentLock.LockAsync(cancellationToken).ConfigureAwait(false))
        {
            runId = _appHostRunId;
            attachmentToken = _appHostToken;
            if (options.ExpectedAppHostRunId is { } expectedRunId && expectedRunId != runId)
            {
                return new ResourceCommandResponseViewModel { Kind = ResourceCommandResponseKind.Cancelled };
            }
        }

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
            using var combinedTokens = CancellationTokenSource.CreateLinkedTokenSource(_clientCancellationToken, cancellationToken, attachmentToken);

            // Keep the header and lifetime from the same attachment even if rollover happens after validation.
            var response = await _client!.ExecuteResourceCommandAsync(request, headers: GetSessionHeaders(runId), cancellationToken: combinedTokens.Token);

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
        catch (OperationCanceledException) when (attachmentToken.IsCancellationRequested || _clientCancellationToken.IsCancellationRequested)
        {
            var errorMessage = _loc[nameof(DashboardResources.ResourceCommandAppHostDisconnected)];
            return new ResourceCommandResponseViewModel
            {
                Kind = ResourceCommandResponseKind.Failed,
                ErrorMessage = errorMessage,
                Message = errorMessage
            };
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled && (attachmentToken.IsCancellationRequested || _clientCancellationToken.IsCancellationRequested))
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

    public async Task<string> UploadFileAsync(Stream fileStream, string fileName, long expectedSize, int interactionId, string inputName, string appHostRunId, CancellationToken cancellationToken)
    {
        EnsureInitialized();

        CancellationToken attachmentToken;
        using (await _attachmentLock.LockAsync(cancellationToken).ConfigureAwait(false))
        {
            if (appHostRunId != _appHostRunId)
            {
                throw new OperationCanceledException("The interaction's AppHost run is no longer attached.");
            }
            attachmentToken = _appHostToken;
        }

        // A delayed file selection belongs to the dialog's originating run, even when interaction IDs
        // are reused after a restart. Keep that run's header and cancellation lifetime together.
        using var combinedTokens = CancellationTokenSource.CreateLinkedTokenSource(_clientCancellationToken, cancellationToken, attachmentToken);
        using var call = _client!.UploadFile(headers: GetSessionHeaders(appHostRunId), cancellationToken: combinedTokens.Token);

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
                chunk.InteractionId = interactionId;
                chunk.InputName = inputName;
            }

            await call.RequestStream.WriteAsync(chunk, combinedTokens.Token).ConfigureAwait(false);
            isFirst = false;
        }

        // Handle case where the file was empty — still send filename.
        if (isFirst)
        {
            await call.RequestStream.WriteAsync(new UploadFileChunk { FileName = fileName, InteractionId = interactionId, InputName = inputName }, combinedTokens.Token).ConfigureAwait(false);
        }

        await call.RequestStream.CompleteAsync().ConfigureAwait(false);

        var response = await call.ResponseAsync.ConfigureAwait(false);
        return response.FileId;
    }

    public async IAsyncEnumerable<WatchTerminalsUpdate> SubscribeTerminalsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        EnsureInitialized();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_clientCancellationToken, cancellationToken);
        var errorCount = 0;

        // Each subscriber owns its RPC, including recovery. Reopening it supplies a fresh snapshot, so neither the
        // dock nor a detached window has to reconstruct changes missed during a disconnect.
        while (true)
        {
            await WhenConnected.WaitAsync(cts.Token).ConfigureAwait(false);

            var unsupported = false;
            var updates = WatchTerminalsCoreAsync(cts.Token).GetAsyncEnumerator(cts.Token);
            await using (updates.ConfigureAwait(false))
            {
                while (true)
                {
                    bool hasNext;
                    try
                    {
                        hasNext = await updates.MoveNextAsync().ConfigureAwait(false);
                    }
                    catch (RpcException ex)
                    {
                        cts.Token.ThrowIfCancellationRequested();
                        unsupported = ex.StatusCode == StatusCode.Unimplemented;
                        if (unsupported)
                        {
                            // Older AppHosts can serve the dashboard without implementing the terminal RPC.
                            _logger.LogWarning("Server does not support terminals.");
                        }
                        else
                        {
                            _logger.LogWarning(ex, "Terminal watch stream disconnected. Retrying.");
                        }
                        break;
                    }

                    if (!hasNext)
                    {
                        break;
                    }

                    errorCount = 0;
                    yield return updates.Current;
                }
            }

            if (unsupported)
            {
                yield break;
            }

            // Normal stream completion also needs recovery. Dispose the old RPC before backing off, and cancel
            // both connection waits and backoff when the subscriber goes away or the dashboard client is disposed.
            var delay = TimeSpan.FromSeconds(Math.Min(Math.Pow(2, errorCount), 15));
            errorCount = Math.Min(errorCount + 1, 4);
            await Task.Delay(delay, cts.Token).ConfigureAwait(false);
        }
    }

    private async IAsyncEnumerable<WatchTerminalsUpdate> WatchTerminalsCoreAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var call = _client!.WatchTerminals(new WatchTerminalsRequest(), headers: GetSessionHeaders(), cancellationToken: cancellationToken);
        await foreach (var update in call.ResponseStream.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return update;
        }
    }

    public async Task CloseTerminalAsync(string terminalId, CancellationToken cancellationToken)
    {
        EnsureInitialized();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_clientCancellationToken, cancellationToken, _appHostToken);
        await _client!.CloseTerminalAsync(
            new CloseTerminalRequest { TerminalId = terminalId },
            headers: GetSessionHeaders(),
            cancellationToken: cts.Token).ConfigureAwait(false);
    }

    public async Task<Stream> AttachTerminalAsync(string terminalId, CancellationToken cancellationToken)
    {
        EnsureInitialized();

        // The call outlives this method, so the linked CTS cannot be scoped with `using` here. Link to the client
        // token anyway so a dashboard-wide disconnect tears the tunnel down instead of leaking it.
        var combinedTokens = CancellationTokenSource.CreateLinkedTokenSource(_clientCancellationToken, cancellationToken, _appHostToken);
        var call = _client!.AttachTerminal(headers: GetSessionHeaders(), cancellationToken: combinedTokens.Token);
        var stream = new GrpcTerminalClientStream(call, terminalId, combinedTokens);

        try
        {
            // The AppHost blocks on the selector frame before wiring the call to Hex1b, so send it eagerly rather than
            // waiting for the browser's first HMP1 frame — otherwise nothing streams until the user types.
            await stream.SendSelectorAsync(combinedTokens.Token).ConfigureAwait(false);
        }
        catch
        {
            stream.Dispose();
            throw;
        }

        return stream;
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
            _appHostCts.Cancel();

            await TaskHelpers.WaitIgnoreCancelAsync(_connection, _logger, "Unexpected error from connection task.").ConfigureAwait(false);
            _appHostCts.Dispose();
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
    private sealed record ConsoleLogAttachment(IDisposable? Retention, IResourceRepositoryWriter Writer, string RunId, CancellationToken Token) : IDisposable
    {
        public void Dispose() => Retention?.Dispose();
    }

}
