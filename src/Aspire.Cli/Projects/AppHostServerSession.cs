// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Cli.Configuration;
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
    private IAppHostRpcClient? _rpcClient;
    private bool _disposed;

    internal AppHostServerSession(
        Process serverProcess,
        OutputCollector output,
        string socketPath,
        string authenticationToken,
        ILogger logger)
    {
        _serverProcess = serverProcess;
        _output = output;
        _socketPath = socketPath;
        _authenticationToken = authenticationToken;
        _logger = logger;
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
    /// <returns>The started AppHost server session.</returns>
    internal static AppHostServerSession Start(
        IAppHostServerProject appHostServerProject,
        Dictionary<string, string>? environmentVariables,
        bool debug,
        ILogger logger)
    {
        var currentPid = Environment.ProcessId;
        var serverEnvironmentVariables = environmentVariables is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(environmentVariables);

        var authenticationToken = TokenGenerator.GenerateToken();
        serverEnvironmentVariables[KnownConfigNames.RemoteAppHostToken] = authenticationToken;

        var (socketPath, serverProcess, serverOutput) = appHostServerProject.Run(
            currentPid,
            serverEnvironmentVariables,
            debug: debug);

        return new AppHostServerSession(
            serverProcess,
            serverOutput,
            socketPath,
            authenticationToken,
            logger);
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
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error killing AppHost server process");
            }
        }

        _serverProcess.Dispose();
    }
}

/// <summary>
/// Factory for creating <see cref="IAppHostServerSession"/> instances.
/// </summary>
internal sealed class AppHostServerSessionFactory : IAppHostServerSessionFactory
{
    private readonly IAppHostServerProjectFactory _projectFactory;
    private readonly ILogger<AppHostServerSession> _logger;

    public AppHostServerSessionFactory(
        IAppHostServerProjectFactory projectFactory,
        ILogger<AppHostServerSession> logger)
    {
        _projectFactory = projectFactory;
        _logger = logger;
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
            _logger);

        return new AppHostServerSessionResult(
            Success: true,
            Session: session,
            BuildOutput: prepareResult.Output,
            ChannelName: prepareResult.ChannelName);
    }
}
