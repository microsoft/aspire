// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREBROWSERLOGS001 // Type is for evaluation purposes only

using System.Globalization;
using Aspire.Hosting.Browsers.Resources;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

internal delegate ISafariDriverProcess SafariDriverProcessFactory(string driverPath, ILogger<BrowserLogsSessionManager> logger);

internal delegate ISafariWebDriverClient SafariWebDriverClientFactory(Uri endpoint, string driverPath);

internal delegate Task<IBrowserLogsBidiConnection> BrowserLogsBidiConnectionFactory(
    Uri webSocketUri,
    Func<BrowserLogsBidiProtocolEvent, ValueTask> eventHandler,
    ILogger<BrowserLogsSessionManager> logger,
    CancellationToken cancellationToken);

internal sealed class SafariWebDriverHostProvider : IBrowserHostProvider
{
    private readonly BrowserLogsBidiConnectionFactory _createBidiConnection;
    private readonly SafariWebDriverClientFactory _createWebDriverClient;
    private readonly ILogger<BrowserLogsSessionManager> _logger;
    private readonly SafariDriverProcessFactory _startDriver;
    private readonly TimeProvider _timeProvider;

    public SafariWebDriverHostProvider(ILogger<BrowserLogsSessionManager> logger, TimeProvider timeProvider)
        : this(
            logger,
            timeProvider,
            SafariDriverProcessLauncher.Start,
            static (endpoint, driverPath) => new SafariWebDriverClient(endpoint, driverPath),
            static async (webSocketUri, eventHandler, logger, cancellationToken) =>
                await BrowserLogsBidiConnection.ConnectAsync(webSocketUri, eventHandler, logger, cancellationToken).ConfigureAwait(false))
    {
    }

    internal SafariWebDriverHostProvider(
        ILogger<BrowserLogsSessionManager> logger,
        TimeProvider timeProvider,
        SafariDriverProcessFactory startDriver,
        SafariWebDriverClientFactory createWebDriverClient,
        BrowserLogsBidiConnectionFactory createBidiConnection)
    {
        _createBidiConnection = createBidiConnection;
        _createWebDriverClient = createWebDriverClient;
        _logger = logger;
        _startDriver = startDriver;
        _timeProvider = timeProvider;
    }

    public async Task<BrowserHostLease> AcquireAsync(BrowserConfiguration configuration, CancellationToken cancellationToken)
    {
        if (configuration.Profile is { } profile)
        {
            // Chromium profiles map to concrete user-data directories. Safari WebDriver does not expose an equivalent
            // safe per-session profile selector for BrowserLogs, so fail instead of accidentally using shared Safari state.
            throw new InvalidOperationException(string.Format(
                CultureInfo.CurrentCulture,
                BrowserMessageStrings.BrowserLogsSafariProfileNotSupported,
                BrowserLogsBuilderExtensions.ProfileConfigurationKey,
                profile));
        }

        var driver = SafariBrowserResolver.TryResolveDriver(configuration.Browser)
            ?? throw new InvalidOperationException(SafariBrowserResolver.GetUnableToLocateDriverMessage(configuration.Browser));

        var host = await SafariWebDriverHost.StartAsync(
            driver,
            _logger,
            _timeProvider,
            _startDriver,
            _createWebDriverClient,
            _createBidiConnection,
            cancellationToken).ConfigureAwait(false);

        return new BrowserHostLease(host, async _ => await host.DisposeAsync().ConfigureAwait(false));
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}

internal sealed class SafariWebDriverHost : IBrowserHost
{
    private const int MaxDriverStartAttempts = 3;
    private static readonly TimeSpan s_deleteSessionTimeout = TimeSpan.FromSeconds(3);

    private readonly BrowserLogsBidiConnectionFactory _createBidiConnection;
    private readonly ISafariDriverProcess _driverProcess;
    private readonly ILogger<BrowserLogsSessionManager> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly ISafariWebDriverClient _webDriverClient;
    private int _disposed;

    private SafariWebDriverHost(
        SafariBrowserDriver driver,
        ISafariDriverProcess driverProcess,
        ISafariWebDriverClient webDriverClient,
        SafariWebDriverSession webDriverSession,
        BrowserLogsBidiConnectionFactory createBidiConnection,
        ILogger<BrowserLogsSessionManager> logger,
        TimeProvider timeProvider)
    {
        _createBidiConnection = createBidiConnection;
        _driverProcess = driverProcess;
        _logger = logger;
        _timeProvider = timeProvider;
        _webDriverClient = webDriverClient;
        BrowserDisplayName = driver.DisplayName;
        Identity = new BrowserHostIdentity(driver.DriverPath, Path.GetDirectoryName(driver.DriverPath) ?? driver.DriverPath);
        WebDriverSession = webDriverSession;
    }

    public BrowserHostIdentity Identity { get; }

    public BrowserHostOwnership Ownership => BrowserHostOwnership.Owned;

    public Uri? DebugEndpoint => null;

    public int? ProcessId => _driverProcess.ProcessId;

    public string BrowserDisplayName { get; }

    public Task Termination => _driverProcess.ProcessTask;

    internal SafariWebDriverSession WebDriverSession { get; }

    public static async Task<SafariWebDriverHost> StartAsync(
        SafariBrowserDriver driver,
        ILogger<BrowserLogsSessionManager> logger,
        TimeProvider timeProvider,
        SafariDriverProcessFactory startDriver,
        SafariWebDriverClientFactory createWebDriverClient,
        BrowserLogsBidiConnectionFactory createBidiConnection,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaxDriverStartAttempts; attempt++)
        {
            var driverProcess = startDriver(driver.DriverPath, logger);
            var webDriverClient = createWebDriverClient(driverProcess.WebDriverEndpoint, driver.DriverPath);

            try
            {
                var webDriverSession = await webDriverClient.CreateSessionAsync(driverProcess.ProcessTask, cancellationToken).ConfigureAwait(false);
                return new SafariWebDriverHost(driver, driverProcess, webDriverClient, webDriverSession, createBidiConnection, logger, timeProvider);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested && attempt < MaxDriverStartAttempts && driverProcess.ProcessTask.IsCompleted)
            {
                // A best-effort free port can race with another process before safaridriver binds, which makes the driver
                // exit before it accepts New Session. Retry the whole driver/WebDriver bootstrap with a new local port.
                logger.LogDebug(ex, "safaridriver exited before accepting a WebDriver session on attempt {Attempt}; retrying with a new loopback port.", attempt);
                await DisposeClientAndProcessAsync(webDriverClient, driverProcess).ConfigureAwait(false);
            }
            catch
            {
                await DisposeClientAndProcessAsync(webDriverClient, driverProcess).ConfigureAwait(false);
                throw;
            }
        }

        throw new InvalidOperationException("Unable to start Safari WebDriver session.");
    }

    public async Task<IBrowserPageSession> CreatePageSessionAsync(
        string sessionId,
        Uri url,
        BrowserConnectionDiagnosticsLogger connectionDiagnostics,
        Func<BrowserDiagnosticEvent, ValueTask> eventHandler,
        CancellationToken cancellationToken)
    {
        return await SafariBidiPageSession.StartAsync(
            this,
            sessionId,
            url,
            connectionDiagnostics,
            eventHandler,
            _logger,
            _timeProvider,
            _createBidiConnection,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            using var deleteCts = new CancellationTokenSource(s_deleteSessionTimeout);
            await _webDriverClient.DeleteSessionAsync(WebDriverSession.SessionId, deleteCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to delete Safari WebDriver session '{WebDriverSessionId}'.", WebDriverSession.SessionId);
        }
        finally
        {
            await DisposeClientAndProcessAsync(_webDriverClient, _driverProcess).ConfigureAwait(false);
        }
    }

    private static async Task DisposeClientAndProcessAsync(ISafariWebDriverClient webDriverClient, ISafariDriverProcess driverProcess)
    {
        try
        {
            webDriverClient.Dispose();
        }
        finally
        {
            await driverProcess.DisposeAsync().ConfigureAwait(false);
        }
    }
}
