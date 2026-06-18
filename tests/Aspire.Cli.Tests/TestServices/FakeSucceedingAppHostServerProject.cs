// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Cli.Configuration;
using Aspire.Cli.Projects;
using Aspire.Cli.Utils;

namespace Aspire.Cli.Tests.TestServices;

/// <summary>
/// <see cref="IAppHostServerProject"/> whose <see cref="PrepareAsync"/> returns success.
/// Used with a fake <see cref="IAppHostServerSessionFactory"/> that bypasses
/// <see cref="AppHostServerSession"/> so <see cref="Run"/> is never called.
/// </summary>
internal sealed class FakeSucceedingAppHostServerProject(string appDirectoryPath) : IAppHostServerProject, IDisposable
{
    public string AppDirectoryPath { get; } = appDirectoryPath;

    public string GetInstanceIdentifier() => AppDirectoryPath;

    public Task<AppHostServerPrepareResult> PrepareAsync(
        string sdkVersion,
        IEnumerable<IntegrationReference> integrations,
        string? requestedChannel = null,
        string? packageSourceOverride = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new AppHostServerPrepareResult(Success: true, Output: null));

    public (string SocketPath, Process Process, OutputCollector OutputCollector) Run(
        int hostPid,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        string[]? additionalArgs = null,
        bool debug = false) =>
        throw new NotSupportedException("Run should not be invoked when using a fake session starter.");

    public void Dispose()
    {
    }
}
