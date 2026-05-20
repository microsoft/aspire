// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Cli.Configuration;
using Aspire.Cli.Projects;
using Aspire.Cli.Utils;

namespace Aspire.Cli.Tests.TestServices;

/// <summary>
/// <see cref="IAppHostServerProject"/> whose <see cref="PrepareAsync"/> returns failure so
/// callers (e.g. <see cref="Aspire.Cli.Scaffolding.ScaffoldingService"/>) take the early-out
/// path without touching the network, the dotnet CLI, or template restore. Useful for tests
/// that exercise the channel/config write side-effects that happen BEFORE
/// <c>AppHostServerProject.PrepareAsync</c>.
/// </summary>
internal sealed class FakeFailingAppHostServerProject(string appDirectoryPath) : IAppHostServerProject
{
    public string AppDirectoryPath { get; } = appDirectoryPath;

    public string GetInstanceIdentifier() => AppDirectoryPath;

    public Task<AppHostServerPrepareResult> PrepareAsync(
        string sdkVersion,
        IEnumerable<IntegrationReference> integrations,
        string? requestedChannel = null,
        string? packageSourceOverride = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new AppHostServerPrepareResult(Success: false, Output: null));

    public (string SocketPath, Process Process, OutputCollector OutputCollector) Run(
        int hostPid,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        string[]? additionalArgs = null,
        bool debug = false) =>
        throw new NotSupportedException("Run should not be invoked when PrepareAsync fails.");
}
