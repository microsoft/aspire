// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Text;
using Aspire.Hosting.Native.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Native.Authoring;

/// <summary>Configures framework services without giving the AppHost transport or runtime ownership.</summary>
public sealed class NativeBuilderOptions
{
    /// <summary>Gets the stable application source root, defaulting to the launch working directory, not the build output directory.</summary>
    public string? ApplicationRoot { get; init; }
    /// <summary>Gets the application identity within its root, defaulting to the root directory name.</summary>
    public string? ApplicationName { get; init; }
    /// <summary>Gets the framework clock used for connection deadlines and owned-process cleanup.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
    /// <summary>Gets the provider activation cleanup and owned-process shutdown policies.</summary>
    public NativeProviderActivationOptions ProviderActivation { get; init; } = new();
    /// <summary>Gets classified framework logging; credentials and provider console output are never logged here.</summary>
    public ILogger Logger { get; init; } = NullLogger.Instance;
    /// <summary>Gets caller-owned configuration layered over environment variables and below application arguments.</summary>
    public IConfiguration? Configuration { get; init; }
}

internal sealed class NativeBuilderSettings
{
    internal NativeBuilderOptions Options { get; private init; } = new();
    internal string? Endpoint { get; private set; }
    internal string ApplicationDirectory { get; private set; } = Directory.GetCurrentDirectory();
    internal string VolumePrefix { get; private set; } = CreateVolumePrefix(Directory.GetCurrentDirectory(), null);
    internal ConfigurationManager Configuration { get; } = new();
    internal Dictionary<string, (string Executable, string[] Arguments)> Providers { get; } = new(StringComparer.Ordinal);
    internal string? ExitOnCompletion { get; private set; }
    internal static NativeBuilderSettings FromArgs(string[] args, NativeBuilderOptions options)
    {
        var settings = new NativeBuilderSettings { Options = options };
        settings.Configuration.AddEnvironmentVariables();
        if (options.Configuration is { } supplied)
        {
            settings.Configuration.AddConfiguration(supplied);
        }
        var applicationArguments = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var applicationRoot = options.ApplicationRoot ?? Directory.GetCurrentDirectory();
        var applicationName = options.ApplicationName;
        string? volumePrefix = null;
        // Framework flags use --native-provider=package=executable[|arg...].
        // Split once for the flag and package; arguments remain separate process
        // arguments, never an interpolated shell command.
        foreach (var arg in args)
        {
            var parts = arg.TrimStart('-').Split('=', 2);
            if (parts.Length != 2)
            {
                throw new ArgumentException("AppHost configuration must use --name=value.");
            }
            var value = parts[1];
            switch (parts[0])
            {
                case "native-endpoint": settings.Endpoint = value; break;
                case "native-volume-prefix": volumePrefix = value; break;
                case "native-application-root": applicationRoot = value; break;
                case "native-application-name": applicationName = value; break;
                case "native-exit-on-completion": settings.ExitOnCompletion = value; break;
                case "native-provider":
                    var provider = value.Split('=', 2);
                    if (provider.Length != 2)
                    {
                        throw new ArgumentException("Provider configuration requires package=executable.");
                    }
                    var command = provider[1].Split('|');
                    settings.Providers.Add(provider[0], (command[0], command[1..]));
                    break;
                case "publish": throw new NotSupportedException("Native builder publishing is not supported.");
                default: applicationArguments.Add(parts[0], value); break;
            }
        }

        settings.Configuration.AddInMemoryCollection(applicationArguments);
        settings.Endpoint ??= settings.Configuration["ASPIRE_NATIVE_RPC_ENDPOINT"];
        settings.VolumePrefix = volumePrefix ?? CreateVolumePrefix(applicationRoot, applicationName);
        settings.ApplicationDirectory = Path.GetFullPath(applicationRoot);
        return settings;
    }

    private static string CreateVolumePrefix(string applicationRoot, string? applicationName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationRoot);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(applicationRoot));
        applicationName ??= Path.GetFileName(root) is { Length: > 0 } name ? name : "app";
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationName);
        var canonicalRoot = root.Replace(Path.DirectorySeparatorChar, '/');
        if (OperatingSystem.IsWindows())
        {
            canonicalRoot = AsciiLower(canonicalRoot);
            applicationName = AsciiLower(applicationName);
        }

        // Match native-builder.mts: UTF-8(root + NUL + name), FNV-1a/64,
        // lowercase 16-digit hex. The NUL keeps root/name pairs unambiguous.
        // This nonsecurity hash avoids a crypto dependency in the standalone
        // client and has a small, identical implementation in both languages.
        // https://www.ietf.org/archive/id/draft-eastlake-fnv-35.html
        var hash = 14695981039346656037UL;
        foreach (var value in Encoding.UTF8.GetBytes($"{canonicalRoot}\0{applicationName}"))
        {
            hash = unchecked((hash ^ value) * 1099511628211UL);
        }
        var label = AsciiLower(applicationName[..Math.Min(applicationName.Length, 40)]);
        label = new string(label.Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-' ? c : '_').ToArray());

        return $"aspire-{label}-{hash:x16}";
    }

    private static string AsciiLower(string value) => new(value.Select(c => c is >= 'A' and <= 'Z' ? (char)(c + ('a' - 'A')) : c).ToArray());
}

/// <summary>Materializes a sealed transport plan and delegates all scheduling, values and retirement to the native server.</summary>
public sealed class NativeApplication : IAsyncDisposable
{
    private readonly DistributedApplicationBuilder _initial;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;
    private readonly List<NativeProviderActivation> _providers = [];
    private NativeClient? _client;
    private ApplicationWorkspace? _workspace;
    private ApplicationExecution? _execution;
    private NativeBuildContext? _context;
    private readonly SemaphoreSlim _operations = new(1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Lock _disposalGate = new();
    private Task? _disposal;
    private bool _disposed;
    internal NativeApplication(DistributedApplicationBuilder builder)
    {
        _initial = builder;
        _clock = builder.Settings.Options.TimeProvider;
        _logger = builder.Settings.Options.Logger;
    }
    /// <summary>Starts the initial declaration and waits for native readiness.</summary>
    /// <param name="cancellationToken">Cancels startup waits and retires accepted native work.</param>
    /// <returns>A task completing after native authenticated readiness.</returns>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _operations.WaitAsync(operation.Token).ConfigureAwait(false);
        try
        {
            await StartCoreAsync(operation.Token).ConfigureAwait(false);
        }
        finally
        {
            _operations.Release();
        }
    }
    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_client is not null)
        {
            throw new InvalidOperationException("The application has already started.");
        }
        var endpoint = _initial.Settings.Endpoint ?? throw new InvalidOperationException("The framework requires a native server endpoint.");
        var token = _initial.Settings.Configuration["ASPIRE_NATIVE_RPC_TOKEN"]
            ?? throw new InvalidOperationException("The framework requires native authentication.");
        try
        {
            _client = await NativeClient.ConnectAsync(IPEndPoint.Parse(endpoint), token, TimeSpan.FromSeconds(15),
                _clock, cancellationToken).ConfigureAwait(false);
            foreach (var package in _initial.Providers.Keys)
            {
                var command = _initial.Settings.Providers.TryGetValue(package, out var selected) ? selected :
                    throw new InvalidOperationException($"Framework provider configuration is missing for '{package}'.");
                _providers.Add(await NativeProviderActivation.StartAsync(_client, package, command.Executable,
                    command.Arguments, _initial.Settings.Options.ProviderActivation, _clock, _logger, cancellationToken).ConfigureAwait(false));
            }
            _workspace = await _client.Server.CreateApplicationWorkspaceAsync(cancellationToken).ConfigureAwait(false);
            await ApplyAsync(_initial, true, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception startFailure)
        {
            try
            {
                await DisposeCoreAsync().ConfigureAwait(false);
            }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException(startFailure, cleanupFailure);
            }
            throw;
        }
    }
    /// <summary>Replaces authored intent through the native staged revision transaction, not a client runtime graph.</summary>
    /// <param name="next">The next authored command plan.</param>
    /// <param name="cancellationToken">Cancels waits without undoing an adopted native revision.</param>
    /// <returns>A task completing after native revision readiness.</returns>
    public async Task UpdateAsync(DistributedApplicationBuilder next, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _operations.WaitAsync(operation.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_workspace is null)
            {
                throw new InvalidOperationException("The application has not started.");
            }
            if (next.Providers.Keys.Any(package => !_initial.Providers.ContainsKey(package)))
            {
                throw new NotSupportedException("Adding a provider after activation is not yet supported.");
            }
            await ApplyAsync(next, false, operation.Token).ConfigureAwait(false);
        }
        finally
        {
            _operations.Release();
        }
    }
    private async Task ApplyAsync(DistributedApplicationBuilder builder, bool initial, CancellationToken token)
    {
        var revision = await _workspace!.BeginRevisionAsync(token).ConfigureAwait(false);
        NativeBuildContext context;
        try
        {
            context = await builder.MaterializeAsync(revision, token).ConfigureAwait(false);
            if (initial && builder.Pipeline.HasCustomizations)
            {
                var model = System.Text.Json.Nodes.JsonNode.Parse(await revision.ExportPublishModelAsync(token).ConfigureAwait(false))!.AsObject();
                await builder.Pipeline.ExecuteAsync(_client!.Server, new(model, builder.AppHostDirectory, builder.AppHostDirectory,
                    builder.Configuration["environment"] ?? "Development", token) { IsRunMode = true }, "before-start").ConfigureAwait(false);
            }
        }
        catch
        {
            await _workspace.AbortRevisionAsync(revision, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        try
        {
            await _workspace.CommitRevisionAsync(revision, token).ConfigureAwait(false);
        }
        catch
        {
            // Validation can fail before adoption; cleanup can fail after it.
            // The native workspace alone decides whether this is still pending.
            // Abort cannot reopen an adopted generation, and its rejection must
            // never hide the original commit/retirement failure.
            try
            {
                await _workspace.AbortRevisionAsync(revision, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                _logger.LogWarning("Native revision failure could not be discarded as pending authoring.");
            }
            throw;
        }
        _execution = initial ? await revision.StartApplicationAsync(token).ConfigureAwait(false) :
            await _workspace.GetApplicationExecutionAsync(token).ConfigureAwait(false);
        _context = context;
        await _execution.WaitApplicationReadyAsync(60000, token).ConfigureAwait(false);
    }
    /// <summary>Waits for a selected executable's native exit status.</summary>
    /// <typeparam name="T">The authored resource type.</typeparam>
    /// <param name="resource">The executable in the currently adopted materialization.</param>
    /// <param name="cancellationToken">Cancels this observation, not the native workload itself.</param>
    /// <returns>A task that faults for unsuccessful native exit.</returns>
    public async Task WaitForCompletionAsync<T>(IResourceBuilder<T> resource, CancellationToken cancellationToken = default) where T : Resource
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _operations.WaitAsync(operation.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_execution is null || _context is null)
            {
                throw new InvalidOperationException("The application has not started.");
            }
            if (!await _execution.WaitResourceCompletionAsync(_context.Declaration(resource.Resource), 60000, operation.Token).ConfigureAwait(false))
            {
                throw new InvalidOperationException($"The executable '{resource.Resource.Name}' failed.");
            }
        }
        finally
        {
            _operations.Release();
        }
    }
    /// <summary>Runs until cancellation or a host stop request; finite executables may select exit-on-completion configuration.</summary>
    /// <param name="cancellationToken">Requests ordered application shutdown.</param>
    /// <returns>A task completing after native retirement and provider shutdown.</returns>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        ConsoleCancelEventHandler shutdown = (_, args) => { args.Cancel = true; lifetime.Cancel(); };
        Console.CancelKeyPress += shutdown;
        try
        {
            await StartAsync(lifetime.Token).ConfigureAwait(false);
            if (_initial.Settings.ExitOnCompletion is { } name)
            {
                var resource = _initial.Resources.Single(resource => resource.Name == name);
                if (!await _execution!.WaitResourceCompletionAsync(_context!.Declaration(resource), 60000, lifetime.Token).ConfigureAwait(false))
                {
                    throw new InvalidOperationException($"The executable '{name}' failed.");
                }
            }
            else
            {
                var run = await _client!.Server.CreateApplicationLifetimeAsync(lifetime.Token).ConfigureAwait(false);
                await run.WaitForStopAsync(lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            Console.CancelKeyPress -= shutdown;
            await DisposeAsync().ConfigureAwait(false);
        }
    }
    /// <summary>Retires native authority before closing framework-owned provider and transport lifetimes.</summary>
    /// <returns>The shared cleanup task; repeated callers join the same retirement.</returns>
    public ValueTask DisposeAsync()
    {
        lock (_disposalGate)
        {
            if (_disposal is null)
            {
                _lifetime.Cancel();
                _disposal = DisposeSerializedAsync();
            }

            return new(_disposal);
        }
    }
    private async Task DisposeSerializedAsync()
    {
        await _operations.WaitAsync().ConfigureAwait(false);
        try
        {
            await DisposeCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _operations.Release();
        }
    }
    private async Task DisposeCoreAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        List<Exception> failures = [];
        try
        {
            if (_workspace is not null)
            {
                await _workspace.RetireApplicationWorkspaceAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
        foreach (var provider in _providers)
        {
            try
            {
                await provider.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }
        try
        {
            if (_client is not null)
            {
                await _client.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
        _initial.Settings.Configuration.Dispose();
        if (failures.Count > 0)
        {
            throw new AggregateException("Native application cleanup failed.", failures);
        }
    }
}
