// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Cli.Commands.Sdk;
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
    private readonly FakeAppHostRpcClient _rpcClient = new();

    public string SocketPath => "fake-socket";

    public Process ServerProcess { get; } = Process.GetCurrentProcess();

    public OutputCollector Output { get; } = new();

    public string AuthenticationToken => "fake-token";

    public Task<IAppHostRpcClient> GetRpcClientAsync(CancellationToken cancellationToken)
        => Task.FromResult<IAppHostRpcClient>(_rpcClient);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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
