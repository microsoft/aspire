// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Acquisition;

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
        string? buildChannel = null,
        IdentitySource identityChannelSource = IdentitySource.AssemblyFallback,
        string? logFilePath = null,
        string? identityVersion = null,
        string? identityCommit = null,
        bool identityOverridden = false,
        DirectoryInfo? aspireHomeDirectory = null,
        bool identityOverrideNoticeRequired = false)
    {
        return CreateExecutionContext(
            workspace.WorkspaceRoot,
            identityChannel: identityChannel,
            buildChannel: buildChannel,
            identityChannelSource: identityChannelSource,
            logFilePath: logFilePath,
            identityVersion: identityVersion,
            identityCommit: identityCommit,
            identityOverridden: identityOverridden,
            aspireHomeDirectory: aspireHomeDirectory,
            identityOverrideNoticeRequired: identityOverrideNoticeRequired);
    }

    /// <summary>
    /// Creates a <see cref="CliExecutionContext"/> rooted under the supplied
    /// <paramref name="rootDirectory"/>. All .aspire/* directories are scoped to
    /// that root so concurrent tests do not collide on shared paths.
    /// </summary>
    public static CliExecutionContext CreateExecutionContext(
        DirectoryInfo rootDirectory,
        string identityChannel = "local",
        string? buildChannel = null,
        IdentitySource identityChannelSource = IdentitySource.AssemblyFallback,
        DirectoryInfo? homeDirectory = null,
        DirectoryInfo? hivesDirectory = null,
        DirectoryInfo? packagesDirectory = null,
        bool debugMode = false,
        string? logFilePath = null,
        string? identityVersion = null,
        string? identityCommit = null,
        bool identityOverridden = false,
        DirectoryInfo? identityPackagesDirectory = null,
        DirectoryInfo? aspireHomeDirectory = null,
        bool identityOverrideNoticeRequired = false)
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
            identityChannel: identityChannel,
            buildChannel: buildChannel ?? identityChannel,
            identityChannelSource: identityChannelSource,
            identityVersion: identityVersion,
            identityCommit: identityCommit,
            nugetServiceIndexOverride: null,
            identityOverridden: identityOverridden,
            identityPackagesDirectory: identityPackagesDirectory,
            identityOverrideNoticeRequired: identityOverrideNoticeRequired,
            debugMode: debugMode,
            homeDirectory: homeDirectory,
            packagesDirectory: packagesDirectory,
            aspireHomeDirectory: aspireHomeDirectory);
    }
}
