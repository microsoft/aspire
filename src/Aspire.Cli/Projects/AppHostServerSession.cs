// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Cli.Configuration;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;
using Aspire.Hosting;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Projects;

/// <summary>
/// Implementation of <see cref="IAppHostServerSession"/> that manages an AppHost server process.
/// </summary>
internal sealed class AppHostServerSession : IAppHostServerSession
{
    private readonly string _authenticationToken;
    private readonly ILogger _logger;
    private readonly Process _serverProcess;
    private readonly OutputCollector _output;
    private readonly string _socketPath;
    private readonly ProfilingTelemetry.ActivityScope _activity;
    private IAppHostRpcClient? _rpcClient;
    private bool _disposed;

    internal AppHostServerSession(
        Process serverProcess,
        OutputCollector output,
        string socketPath,
        string authenticationToken,
        ILogger logger,
        ProfilingTelemetry.ActivityScope activity = default)
    {
        _serverProcess = serverProcess;
        _output = output;
        _socketPath = socketPath;
        _authenticationToken = authenticationToken;
        _logger = logger;
        _activity = activity;
    }

    /// <inheritdoc />
    public string SocketPath => _socketPath;

    /// <inheritdoc />
    public Process ServerProcess => _serverProcess;

    /// <inheritdoc />
    public OutputCollector Output => _output;

    /// <summary>
    /// Gets the authentication token for the server session.
    /// </summary>
    public string AuthenticationToken => _authenticationToken;

    /// <summary>
    /// Starts an AppHost server process with an authentication token injected into the server environment.
    /// </summary>
    /// <param name="appHostServerProject">The server project to run.</param>
    /// <param name="environmentVariables">The environment variables to pass to the server.</param>
    /// <param name="debug">Whether to enable debug logging for the server.</param>
    /// <param name="logger">The logger to use for lifecycle diagnostics.</param>
    /// <param name="profilingTelemetry">Optional profiling telemetry for the server process lifetime.</param>
    /// <returns>The started AppHost server session.</returns>
    internal static AppHostServerSession Start(
        IAppHostServerProject appHostServerProject,
        Dictionary<string, string>? environmentVariables,
        bool debug,
        ILogger logger,
        ProfilingTelemetry? profilingTelemetry = null)
    {
        var currentPid = Environment.ProcessId;
        var serverEnvironmentVariables = environmentVariables is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(environmentVariables);

        var authenticationToken = TokenGenerator.GenerateToken();
        serverEnvironmentVariables[KnownConfigNames.RemoteAppHostToken] = authenticationToken;

        var activity = profilingTelemetry is null
            ? default
            : profilingTelemetry.StartAppHostServerLifetime(appHostServerProject.GetType().Name);

        string socketPath;
        Process serverProcess;
        OutputCollector serverOutput;
        try
        {
            (socketPath, serverProcess, serverOutput) = appHostServerProject.Run(
                currentPid,
                serverEnvironmentVariables,
                debug: debug);
        }
        catch (Exception ex)
        {
            activity.SetError(ex.Message);
            activity.Dispose();
            throw;
        }

        activity.SetProcessId(serverProcess.Id);
        activity.SetProcessExecutableName(Path.GetFileName(serverProcess.StartInfo.FileName));

        return new AppHostServerSession(
            serverProcess,
            serverOutput,
            socketPath,
            authenticationToken,
            logger,
            activity);
    }

    /// <inheritdoc />
    public async Task<IAppHostRpcClient> GetRpcClientAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(AppHostServerSession));

        return _rpcClient ??= await AppHostRpcClient.ConnectAsync(_socketPath, _authenticationToken, cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_rpcClient != null)
        {
            await _rpcClient.DisposeAsync();
            _rpcClient = null;
        }

        if (!_serverProcess.HasExited)
        {
            try
            {
                _serverProcess.Kill(entireProcessTree: true);
                _activity.SetError("AppHost server process was terminated during session disposal.");
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error killing AppHost server process");
            }
        }

        if (_serverProcess.HasExited)
        {
            _activity.SetProcessExitCode(_serverProcess.ExitCode);
        }

        _serverProcess.Dispose();
        _activity.Dispose();
    }
}

/// <summary>
/// Factory for creating <see cref="IAppHostServerSession"/> instances.
/// </summary>
internal sealed class AppHostServerSessionFactory : IAppHostServerSessionFactory
{
    private readonly IAppHostServerProjectFactory _projectFactory;
    private readonly ILogger<AppHostServerSession> _logger;
    private readonly ProfilingTelemetry _profilingTelemetry;

    public AppHostServerSessionFactory(
        IAppHostServerProjectFactory projectFactory,
        ILogger<AppHostServerSession> logger,
        ProfilingTelemetry profilingTelemetry)
    {
        _projectFactory = projectFactory;
        _logger = logger;
        _profilingTelemetry = profilingTelemetry;
    }

    /// <inheritdoc />
    public async Task<AppHostServerSessionResult> CreateAsync(
        string appHostPath,
        string sdkVersion,
        IEnumerable<IntegrationReference> integrations,
        Dictionary<string, string>? launchSettingsEnvVars,
        bool debug,
        CancellationToken cancellationToken)
    {
        var appHostServerProject = await _projectFactory.CreateAsync(appHostPath, cancellationToken);

        // Prepare the server (create files + build for dev mode, restore packages for prebuilt mode)
        var prepareResult = await appHostServerProject.PrepareAsync(sdkVersion, integrations, cancellationToken);
        if (!prepareResult.Success)
        {
            return new AppHostServerSessionResult(
                Success: false,
                Session: null,
                BuildOutput: prepareResult.Output,
                ChannelName: prepareResult.ChannelName);
        }

        var session = AppHostServerSession.Start(
            appHostServerProject,
            launchSettingsEnvVars,
            debug,
            _logger,
            _profilingTelemetry);

        return new AppHostServerSessionResult(
            Success: true,
            Session: session,
            BuildOutput: prepareResult.Output,
            ChannelName: prepareResult.ChannelName);
    }
}
