// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Commands.Sdk;
using Aspire.Cli.Processes;
using Aspire.Cli.Projects;
using Aspire.Cli.Utils;
using Aspire.TypeSystem;

namespace Aspire.Cli.Tests.TestServices;

/// <summary>
/// Fake <see cref="IAppHostServerSession"/> that returns a <see cref="FakeAppHostRpcClient"/>
/// without starting a real process or connecting to a socket.
/// </summary>
internal sealed class FakeAppHostServerSession : IAppHostServerSession
{
    private readonly IAppHostRpcClient _rpcClient;
    private readonly TaskCompletionSource<int> _exit = new();

    public FakeAppHostServerSession(IAppHostRpcClient? rpcClient = null)
    {
        _rpcClient = rpcClient ?? new FakeAppHostRpcClient();
    }

    public Func<Task>? StartAsyncCallback { get; init; }

    public Func<CancellationToken, Task<IAppHostRpcClient>>? GetRpcClientAsyncCallback { get; init; }

    public bool StartAsyncCalled { get; private set; }

    public string AuthenticationToken { get; } = "fake-token";

    public string? SocketPath { get; } = "fake.sock";

    public OutputCollector? Output { get; } = new();

    public bool? HasServerExited => _exit.Task.IsCompleted;

    public int? TryGetServerExitCode() => _exit.Task.IsCompletedSuccessfully ? _exit.Task.Result : null;

    public async Task StartAsync()
    {
        StartAsyncCalled = true;
        if (StartAsyncCallback is not null)
        {
            await StartAsyncCallback();
        }
    }

    public Task<int> WaitForExitAsync() => _exit.Task;

    public Task<IAppHostRpcClient> GetRpcClientAsync(CancellationToken cancellationToken)
        => GetRpcClientAsyncCallback is not null
            ? GetRpcClientAsyncCallback(cancellationToken)
            : Task.FromResult(_rpcClient);

    public ValueTask DisposeAsync()
    {
        _exit.TrySetResult(0);
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Fake <see cref="IAppHostServerSessionFactory"/> that hands back a <see cref="FakeAppHostServerSession"/>
/// without building or launching a real AppHost server.
/// </summary>
internal sealed class FakeAppHostServerSessionFactory : IAppHostServerSessionFactory
{
    public IAppHostServerSession? Session { get; init; }

    public Dictionary<string, string>? CapturedEnvironmentVariables { get; private set; }

    public IAppHostServerSession Create(
        IAppHostServerProject appHostServerProject,
        Dictionary<string, string>? environmentVariables,
        bool debug,
        IProcessTreeGracefulShutdownSignaler? gracefulShutdownSignaler,
        IGracefulShutdownWindow? shutdownService,
        bool isolateConsole,
        CancellationToken stopRequested)
    {
        CapturedEnvironmentVariables = environmentVariables is null ? null : new Dictionary<string, string>(environmentVariables);
        return Session ?? new FakeAppHostServerSession();
    }
}

/// <summary>
/// Fake RPC client that returns empty results for all operations.
/// Used to exercise code paths that run after RPC connection without needing a real server.
/// </summary>
internal sealed class FakeAppHostRpcClient : IAppHostRpcClient
{
    public Task<RuntimeSpec> GetRuntimeSpecAsync(string languageId, CancellationToken cancellationToken)
        => Task.FromResult(new RuntimeSpec
        {
            Language = languageId,
            DisplayName = "Fake",
            CodeGenLanguage = "TypeScript",
            DetectionPatterns = ["apphost.ts"],
            Execute = new CommandSpec { Command = "node", Args = ["apphost.js"] }
        });

    public Task<Dictionary<string, string>> ScaffoldAppHostAsync(string languageId, string targetPath, string? projectName, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task<Dictionary<string, string>> GenerateCodeAsync(string languageId, CancellationToken cancellationToken)
        => Task.FromResult(new Dictionary<string, string>());

    public Task<Dictionary<string, string>> GenerateCodeForAssemblyAsync(string languageId, string assemblyName, CancellationToken cancellationToken)
        => Task.FromResult(new Dictionary<string, string>());

    public Task<CapabilitiesInfo> GetCapabilitiesAsync(CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task<CapabilitiesInfo> GetCapabilitiesForAssembliesAsync(IReadOnlyList<string> assemblyNames, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task<T> InvokeAsync<T>(string methodName, object?[] parameters, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task InvokeAsync(string methodName, object?[] parameters, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
