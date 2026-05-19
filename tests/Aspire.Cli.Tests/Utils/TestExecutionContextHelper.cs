// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Tests.Utils;

/// <summary>
/// Shared factory for building <see cref="CliExecutionContext"/> instances in tests.
/// Centralizes the boilerplate of wiring up .aspire/* subdirectories so every test
/// gets workspace-scoped isolation by default.
/// </summary>
internal static class TestExecutionContextHelper
{
    /// <summary>
    /// Creates a <see cref="CliExecutionContext"/> rooted under
    /// <paramref name="workspace"/>.<see cref="TemporaryWorkspace.WorkspaceRoot"/>.
    /// All .aspire/* directories are scoped to the workspace so concurrent tests
    /// do not collide on shared paths.
    /// </summary>
    public static CliExecutionContext CreateExecutionContext(
        this TemporaryWorkspace workspace,
        string identityChannel = "local",
        IReadOnlyDictionary<string, string?>? environmentVariables = null,
        string? logFilePath = null)
    {
        return CreateExecutionContext(
            workspace.WorkspaceRoot,
            identityChannel: identityChannel,
            environmentVariables: environmentVariables,
            logFilePath: logFilePath);
    }

    /// <summary>
    /// Creates a <see cref="CliExecutionContext"/> rooted under the supplied
    /// <paramref name="rootDirectory"/>. All .aspire/* directories are scoped to
    /// that root so concurrent tests do not collide on shared paths.
    /// </summary>
    public static CliExecutionContext CreateExecutionContext(
        DirectoryInfo rootDirectory,
        string identityChannel = "local",
        DirectoryInfo? homeDirectory = null,
        DirectoryInfo? hivesDirectory = null,
        IReadOnlyDictionary<string, string?>? environmentVariables = null,
        DirectoryInfo? packagesDirectory = null,
        bool debugMode = false,
        string? logFilePath = null)
    {
        var root = rootDirectory.FullName;
        hivesDirectory ??= new DirectoryInfo(Path.Combine(root, ".aspire", "hives"));
        homeDirectory ??= new DirectoryInfo(Path.Combine(root, ".home"));
        var cacheDirectory = new DirectoryInfo(Path.Combine(root, ".aspire", "cache"));
        var sdksDirectory = new DirectoryInfo(Path.Combine(root, ".aspire", "sdks"));
        var logsDirectory = new DirectoryInfo(Path.Combine(root, ".aspire", "logs"));
        logFilePath ??= Path.Combine(logsDirectory.FullName, "test.log");

        return new CliExecutionContext(
            rootDirectory,
            hivesDirectory,
            cacheDirectory,
            sdksDirectory,
            logsDirectory,
            logFilePath,
            debugMode: debugMode,
            environmentVariables: environmentVariables,
            homeDirectory: homeDirectory,
            packagesDirectory: packagesDirectory,
            identityChannel: identityChannel);
    }
}
