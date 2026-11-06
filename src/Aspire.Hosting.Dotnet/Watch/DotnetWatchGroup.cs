// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREEXTENSION001, ASPIREPROJECTS001, ASPIREWATCH001

using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Aspire.Hosting.ApplicationModel;
using Aspire.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Dotnet.Watch;

internal sealed class DotnetWatchGroup : IAsyncDisposable
{
    private static readonly UTF8Encoding s_utf8 = new(false, true);
    private readonly IDistributedApplicationBuilder _builder;
    private readonly IServiceProvider _services;
    private readonly DotnetWatchToolInstallation _tool;
    private readonly DotnetWatchRoot[] _roots;
    private readonly DotnetWatchRegistration[] _registrations;
    private readonly string _cli;
    private readonly string[] _prefix;
    private readonly int _index;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _statusConnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _controlLock = new(1, 1);
    private readonly SemaphoreSlim _activationLock = new(1, 1);
    private readonly List<DotnetWatchMember> _members = [];
    private readonly string _pipe = "aw-" + Guid.NewGuid().ToString("N")[..10];
    private readonly DirectoryInfo _scopeDirectory = Directory.CreateTempSubdirectory("aspire-watch-scope-");
    private NamedPipeServerStream? _status;
    private NamedPipeServerStream? _control;
    private ExecutableResource? _server;
    private ResourceCommandAnnotation? _serverStop;
    private Task? _monitor;
    private Task? _logForwarding;
    private int _failureHandled;
    private int _disposed;
    private volatile bool _enabled = true;
    private volatile bool _active;
    private volatile bool _stopping;
    private volatile bool _shutdown;
    private volatile bool _buildFailed;
    private volatile bool _scopeCompletionExpected;
    private volatile string? _failure;

    internal DotnetWatchGroup(
        IDistributedApplicationBuilder builder, IServiceProvider services, DotnetWatchToolInstallation tool,
        DotnetWatchRoot[] roots, DotnetWatchRegistration[] registrations, string cli, string[] prefix, int index)
    {
        _builder = builder;
        _services = services;
        _tool = tool;
        _roots = roots;
        _registrations = registrations;
        _cli = cli;
        _prefix = prefix;
        _index = index;
        _logger = services.GetRequiredService<ILoggerFactory>().CreateLogger<DotnetWatchGroup>();
    }

    internal bool Enabled => _enabled;
    internal bool Failed => _failure is not null && _enabled;
    internal bool Stopping => _stopping;
    internal CancellationToken StoppingToken => _cancellation.Token;
    internal string? Failure => _failure;

    internal void Configure(DistributedApplicationModel model)
    {
        _status = CreateListener(_pipe + "-s");
        _control = CreateListener(_pipe + "-c");
        var sdk = _roots[0].Project;
        var executable = new ExecutableResource($"__dotnet-watch-{_index}", _cli,
            _roots[0].Metadata.BuildWorkingDirectory ?? Path.GetDirectoryName(_roots[0].Metadata.ProjectPath)!);
        executable.Annotations.Add(NameValidationPolicyAnnotation.None);
        var server = _builder.AddResource(executable)
            .WithArgs(context =>
            {
                foreach (var argument in _prefix)
                {
                    context.Args.Add(argument);
                }
                context.Args.Add(DotnetWatchConfiguration.ServerCommand);
                context.Args.Add(sdk.DotnetPath);
                foreach (var argument in new[] { "exec", _tool.DllPath, "server", "--sdk", sdk.SdkDirectory,
                    "--server", _pipe, "--status-pipe", _pipe + "-s", "--control-pipe", _pipe + "-c" })
                {
                    context.Args.Add(argument);
                }
                foreach (var root in _roots)
                {
                    context.Args.Add("--resource");
                    context.Args.Add(root.Metadata.ProjectPath);
                }
            })
            .WithEnvironment("DOTNET_WATCH_SUPPRESS_LAUNCH_BROWSER", "true")
            .WithEnvironment("DOTNET_WATCH_RESTART_ON_RUDE_EDIT", "true")
            .WithEnvironment("MSBUILDTERMINALLOGGER", "false")
            // AppHost invalidation belongs only to the AppHost, never its services.
            .WithEnvironment(DotnetWatchConfiguration.HostControlPipe, "")
            .WithEnvironment(DotnetWatchConfiguration.HostSession, "")
            .WithEnvironment(DotnetWatchConfiguration.ScopeCompletionPath, Path.Combine(_scopeDirectory.FullName, "completed"))
            .ExcludeFromManifest()
            .ExcludeFromMcp()
            .WithHidden();
        _server = server.Resource;
        foreach (var command in new[] { "start", "stop", "restart" })
        {
            server.WithAnnotation(new ResourceLifecycleCommandOverrideAnnotation(command, original =>
            {
                if (original.Name == "stop")
                {
                    _serverStop = original;
                }
                return CloneCommand(original, original.UpdateState, original.ExecuteCommand, ResourceCommandVisibility.None);
            }));
        }
        server.OnBeforeResourceStarted(async (_, _, cancellationToken) =>
        {
            foreach (var registration in _registrations)
            {
                if (registration.Builds is { } builds)
                {
                    await builds.WaitForBuildCompletionAsync(
                        registration.Resource, _services, cancellationToken).ConfigureAwait(false);
                }
            }
            File.Delete(Path.Combine(_scopeDirectory.FullName, "completed"));
            _scopeCompletionExpected = true;
            _logForwarding ??= ForwardServerLogsAsync(server.Resource);
        });
        foreach (var build in model.Resources.OfType<DotnetProjectBuildResource>().ToArray())
        {
            server.WaitForCompletion(_builder.CreateResourceBuilder(build));
        }
        _builder.Eventing.Subscribe<ResourceStoppedEvent>(_server, async (_, cancellationToken) =>
        {
            if (!_stopping && _enabled)
            {
                await FailAsync("The watch server exited before the watch run stopped.", cancellationToken).ConfigureAwait(false);
            }
        });

        foreach (var registration in _registrations)
        {
            var member = new DotnetWatchMember(this, registration, _builder, _services, _cli, _prefix, _pipe);
            _members.Add(member);
            member.Configure();
            foreach (var rebuilder in model.Resources.Where(resource =>
                resource.Name == registration.Resource.Name + "-rebuilder" &&
                resource is IResourceWithParent<IResource> parent && ReferenceEquals(parent.Parent, registration.Resource)))
            {
                _builder.Eventing.Subscribe<BeforeResourceStartedEvent>(rebuilder, (_, _) =>
                {
                    if (_enabled)
                    {
                        throw new DistributedApplicationException(DotnetWatchMember.RebuildExplanation);
                    }
                    return Task.CompletedTask;
                });
            }
        }
        _monitor = MonitorAsync();
    }

    private static NamedPipeServerStream CreateListener(string name) =>
        new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private async Task ForwardServerLogsAsync(ExecutableResource server)
    {
        try
        {
            // Compiler diagnostics are emitted on the hidden server's console, not the
            // per-application resource pipe. Keep them visible on all sharing consumers.
            await foreach (var batch in _services.GetRequiredService<ResourceLoggerService>()
                .WatchAsync(server).WithCancellation(_cancellation.Token).ConfigureAwait(false))
            {
                foreach (var line in batch)
                {
                    foreach (var member in _members)
                    {
                        member.LogWatchMessage(line.IsErrorMessage, line.Content);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
        }
    }

    internal async Task<bool> WaitUntilReadyAsync(CancellationToken cancellationToken)
    {
        bool ready;
        try
        {
            ready = await _ready.Task.WaitAsync(TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_buildFailed)
            {
                // Compilation is an application failure, not permission to run stale output.
                throw new DistributedApplicationException("The watcher's initial application build failed. Correct the source and retry Start.");
            }
            await FailAsync("Timed out initializing the watch status/control protocol.", cancellationToken).ConfigureAwait(false);
            ready = false;
        }

        // Readiness and fallback compete with the first launch. Serialize that commit
        // with complete restoration/cleanup, even if the readiness task already completed.
        await _activationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_shutdown)
            {
                throw new OperationCanceledException("The experimental watch run is stopping.", _cancellation.Token);
            }
            if (_enabled && _failure is { } failure)
            {
                throw new DistributedApplicationException(failure);
            }
            if (ready && _enabled && _buildFailed)
            {
                throw new DistributedApplicationException("The watcher's initial application build failed. Correct the source and retry Start.");
            }
            _active |= ready && _enabled;
            return ready && _enabled;
        }
        finally
        {
            _activationLock.Release();
        }
    }

    private async Task MonitorAsync()
    {
        try
        {
            var status = ReadStatusAsync();
            var control = _control!.WaitForConnectionAsync(_cancellation.Token);
            if (await Task.WhenAny(status, control).ConfigureAwait(false) == status)
            {
                await status.ConfigureAwait(false);
            }
            await control.ConfigureAwait(false);
            await _statusConnected.Task.WaitAsync(_cancellation.Token).ConfigureAwait(false);
            _ready.TrySetResult(true);
            await status.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
            _ready.TrySetResult(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or UnauthorizedAccessException or ArgumentException)
        {
            if (!_stopping)
            {
                await FailAsync($"Watch transport failed: {exception.Message}", CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private async Task ReadStatusAsync()
    {
        await _status!.WaitForConnectionAsync(_cancellation.Token).ConfigureAwait(false);
        _statusConnected.TrySetResult();
        using var reader = new StreamReader(_status, s_utf8, leaveOpen: true);
        while (true)
        {
            var line = await ReadBoundedLineAsync(reader, _cancellation.Token).ConfigureAwait(false)
                ?? throw new EndOfStreamException("The watch status pipe disconnected.");
            var status = JsonSerializer.Deserialize(line, DotnetWatchJsonContext.Default.DotnetWatchStatus)
                ?? throw new InvalidDataException("The watch server sent a null status.");
            if (status.Projects is null || status.Projects.Any(string.IsNullOrEmpty))
            {
                throw new InvalidDataException("The watch server sent invalid project identities.");
            }
            if (status.Type is not ("building" or "build_complete" or "hot_reload_applied" or "restarting" or "process_started" or "process_exited"))
            {
                throw new InvalidDataException("The watch server sent an unsupported status event.");
            }
            if ((status.Type == "build_complete" && status.Success is null) ||
                (status.Type == "process_exited" && status.ExitCode is null))
            {
                throw new InvalidDataException("The watch server omitted the result of a lifecycle event.");
            }
            if (status.Type == "build_complete")
            {
                _buildFailed = status.Success is not true;
            }
            foreach (var member in _members)
            {
                var root = _roots.Single(root => ReferenceEquals(root.Resource, member.Resource));
                var affected = status.Projects.Any(path => status.Type is "process_started" or "process_exited"
                    ? DotnetWatchProjectGraph.PathComparer.Equals(
                        DotnetWatchProjectGraph.Normalize(path), DotnetWatchProjectGraph.Normalize(root.Metadata.ProjectPath))
                    : root.Projects.ContainsKey(DotnetWatchProjectGraph.Normalize(path)));
                if (affected)
                {
                    await member.HandleStatusAsync(status).ConfigureAwait(false);
                }
            }
        }
    }

    internal static async Task<string?> ReadBoundedLineAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[1];
        var line = new StringBuilder();
        // Status/control records are JSON lines such as {"type":"building","projects":["/repo/App.csproj"]}.
        // Bound accumulation before ReadLine can allocate an arbitrarily large string.
        while (await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false) != 0)
        {
            if (buffer[0] == '\n')
            {
                var result = line.ToString().TrimEnd('\r');
                if (s_utf8.GetByteCount(result) > DotnetWatchProtocol.MaximumMessageBytes)
                {
                    throw new InvalidDataException("The watch status record exceeds the supported size.");
                }
                return result;
            }
            if (line.Length == DotnetWatchProtocol.MaximumMessageBytes)
            {
                throw new InvalidDataException("The watch status record exceeds the supported size.");
            }
            line.Append(buffer[0]);
        }
        if (line.Length != 0)
        {
            throw new EndOfStreamException("The watch server sent a truncated status record.");
        }
        return null;
    }

    internal async Task RestartAsync(string project, CancellationToken cancellationToken)
    {
        await _controlLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_enabled || _failure is not null || _control?.IsConnected is not true)
            {
                throw new IOException("The experimental watch group is unavailable. Restart the watch run.");
            }
            using var buffer = new MemoryStream();
            using (var json = new Utf8JsonWriter(buffer))
            {
                json.WriteStartObject();
                // The pinned command is named rebuild but performs only a process restart.
                json.WriteString("type", "rebuild");
                json.WriteStartArray("projects");
                json.WriteStringValue(project);
                json.WriteEndArray();
                json.WriteEndObject();
            }
            await _control.WriteAsync(buffer.ToArray(), cancellationToken).ConfigureAwait(false);
            await _control.WriteAsync(new byte[] { (byte)'\n' }, cancellationToken).ConfigureAwait(false);
            await _control.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _controlLock.Release();
        }
    }

    internal async Task FailAsync(string reason, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _failureHandled) != 0)
        {
            return;
        }
        await _activationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_shutdown || _failureHandled != 0)
            {
                return;
            }
            _failureHandled = 1;
            _failure = reason;
            _stopping = true;
            var sourceFailure = !_active && (_buildFailed || HasInitialBuildFailure());
            var fallback = !_active && !sourceFailure;
            if (fallback)
            {
                _logger.LogWarning("Experimental .NET watch could not start: {Reason} Cleaning up before ordinary fallback.", reason);
            }
            else
            {
                _logger.LogError("Experimental .NET watch group failed: {Reason} Restart the watch run to recover.", reason);
            }
            if (!fallback)
            {
                foreach (var member in _members)
                {
                    await member.PublishAsync().ConfigureAwait(false);
                }
            }
            var cleanedUp = false;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                cleanedUp = await StopOwnedProcessesAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                _logger.LogError("Timed out cleaning up the failed experimental .NET watch group.");
            }
            if (cleanedUp && fallback)
            {
                foreach (var member in _members)
                {
                    member.RestoreLaunch();
                }
                _enabled = false;
                foreach (var member in _members)
                {
                    await member.PublishAsync().ConfigureAwait(false);
                }
            }
            else if (!cleanedUp)
            {
                // Starting an ordinary replacement before the old owner settles could
                // create duplicate applications or concurrent writes to shared outputs.
                _failure += " Cleanup could not be confirmed; ordinary fallback was refused.";
                _logger.LogError("{Reason}", _failure);
                foreach (var member in _members)
                {
                    await member.PublishAsync().ConfigureAwait(false);
                }
            }
            _ready.TrySetResult(false);
            await _cancellation.CancelAsync().ConfigureAwait(false);
        }
        finally
        {
            _activationLock.Release();
        }
    }

    private bool HasInitialBuildFailure()
    {
        var notifications = _services.GetRequiredService<ResourceNotificationService>();
        return _builder.Resources.OfType<DotnetProjectBuildResource>().Any(build =>
            notifications.TryGetCurrentState(build.Name, out var current) &&
            (current.Snapshot.State?.Text == KnownResourceStates.FailedToStart ||
                (current.Snapshot.State?.Text is { } state && KnownResourceStates.TerminalStates.Contains(state) &&
                    current.Snapshot.ExitCode is { } exitCode && exitCode != 0)));
    }

    internal async Task StopAsync(CancellationToken cancellationToken)
    {
        _shutdown = true;
        await _activationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_cancellation.IsCancellationRequested)
            {
                _stopping = true;
                try
                {
                    if (!await StopOwnedProcessesAsync(cancellationToken).ConfigureAwait(false))
                    {
                        _logger.LogWarning("Some watch-owned processes could not be stopped through their resource lifecycle commands.");
                    }
                }
                finally
                {
                    await _cancellation.CancelAsync().ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _activationLock.Release();
        }
    }

    internal async Task AbortSetupAsync(string reason)
    {
        await _activationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            _failure = reason;
            _enabled = false;
            _stopping = true;
            foreach (var member in _members)
            {
                member.RestoreLaunch();
            }
            if (_server is not null)
            {
                _builder.Resources.Remove(_server);
            }
            _ready.TrySetResult(false);
            await _cancellation.CancelAsync().ConfigureAwait(false);
        }
        finally
        {
            _activationLock.Release();
        }
    }

    private async Task<bool> StopOwnedProcessesAsync(CancellationToken cancellationToken)
    {
        var success = true;
        foreach (var member in _members)
        {
            success &= await member.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        if (_serverStop is not null && _server is not null)
        {
            success &= await ExecuteStopAsync(_server, _serverStop, _services, _logger, cancellationToken).ConfigureAwait(false);
        }
        else if (_server is not null &&
            _services.GetRequiredService<ResourceNotificationService>().TryGetCurrentState(_server.Name, out var current) &&
            current.Snapshot.State?.Text is { } state &&
            state != KnownResourceStates.NotStarted && !KnownResourceStates.TerminalStates.Contains(state))
        {
            _logger.LogError("The lifecycle stop callback for the watch server is unavailable.");
            success = false;
        }
        if (_scopeCompletionExpected && !File.Exists(Path.Combine(_scopeDirectory.FullName, "completed")))
        {
            _logger.LogError("The watch server did not confirm cleanup of its owned process scope.");
            success = false;
        }
        return success;
    }

    internal static async Task<bool> ExecuteStopAsync(
        IResource resource, ResourceCommandAnnotation command, IServiceProvider services, ILogger logger, CancellationToken cancellationToken)
    {
        var notifications = services.GetRequiredService<ResourceNotificationService>();
        // Watch eligibility excludes replicas, so the public model-name lookup resolves
        // exactly one DCP identity without exposing core's instance annotations.
        if (!notifications.TryGetCurrentState(resource.Name, out var current))
        {
            return true;
        }
        var transport = current.Snapshot.Properties.FirstOrDefault(property => property.Name == "watch.transport.state")?.Value as string
            ?? current.Snapshot.State?.Text;
        if (transport is null || transport == KnownResourceStates.NotStarted || KnownResourceStates.TerminalStates.Contains(transport))
        {
            return true;
        }
        try
        {
            var result = await command.ExecuteCommand(new ExecuteCommandContext
            {
                ResourceName = current.ResourceId,
                Services = services,
                Logger = logger,
                Arguments = new InteractionInputCollection([]),
                CancellationToken = cancellationToken
            }).ConfigureAwait(false);
            if (!result.Success)
            {
                logger.LogWarning("Could not stop watch-owned resource '{ResourceName}': {Reason}", resource.Name, result.Message);
            }
            return result.Success;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or DistributedApplicationException)
        {
            logger.LogWarning(exception, "Could not stop watch-owned resource '{ResourceName}' during cleanup.", resource.Name);
            return false;
        }
    }

    internal static ResourceCommandAnnotation CloneCommand(
        ResourceCommandAnnotation original,
        Func<UpdateCommandStateContext, ResourceCommandState> update,
        Func<ExecuteCommandContext, Task<ExecuteCommandResult>> execute,
        ResourceCommandVisibility visibility) =>
        new(original.Name, original.DisplayName, update, execute, original.DisplayDescription,
            original.Arguments, original.ConfirmationMessage, original.IconName, original.IconVariant,
            original.IsHighlighted, visibility, original.ValidateArguments);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await StopAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            _logger.LogWarning("Timed out stopping experimental .NET watch during disposal.");
            await _cancellation.CancelAsync().ConfigureAwait(false);
        }
        if (_monitor is not null)
        {
            await _monitor.ConfigureAwait(false);
        }
        if (_logForwarding is not null)
        {
            await _logForwarding.ConfigureAwait(false);
        }
        if (_status is not null)
        {
            await _status.DisposeAsync().ConfigureAwait(false);
        }
        if (_control is not null)
        {
            await _control.DisposeAsync().ConfigureAwait(false);
        }
        await _controlLock.WaitAsync().ConfigureAwait(false);
        _controlLock.Release();
        _cancellation.Dispose();
        _controlLock.Dispose();
        _activationLock.Dispose();
        _scopeDirectory.Delete(recursive: true);
    }
}
