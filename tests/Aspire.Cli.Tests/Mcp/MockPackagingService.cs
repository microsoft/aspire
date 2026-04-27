// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Backchannel;
using Aspire.Cli.Packaging;
using Aspire.Cli.Tests.TestServices;
using Aspire.Shared;

namespace Aspire.Cli.Tests.Mcp;

internal static class MockPackagingServiceFactory
{
    public static TestPackagingService Create(NuGetPackageCli[]? integrationPackages = null)
    {
        var packages = integrationPackages ?? [];
        return new TestPackagingService
        {
            GetChannelsAsyncCallback = _ =>
            {
                var cache = new FakeNuGetPackageCache
                {
                    GetIntegrationPackagesAsyncCallback = (_, _, _, _) =>
                        Task.FromResult<IEnumerable<NuGetPackageCli>>(packages)
                };
                return Task.FromResult<IEnumerable<PackageChannel>>([PackageChannel.CreateImplicitChannel(cache)]);
            }
        };
    }
}

internal static class TestExecutionContextFactory
{
    public static CliExecutionContext CreateTestContext()
    {
        return new CliExecutionContext(
            new DirectoryInfo(Path.GetTempPath()),
            new DirectoryInfo(Path.Combine(Path.GetTempPath(), "hives")),
            new DirectoryInfo(Path.Combine(Path.GetTempPath(), "cache")),
            new DirectoryInfo(Path.Combine(Path.GetTempPath(), "sdks")),
            new DirectoryInfo(Path.Combine(Path.GetTempPath(), "logs")),
            "test.log");
    }
}

internal sealed class MockAuxiliaryBackchannelMonitor : IAuxiliaryBackchannelMonitor
{
    public IEnumerable<IAppHostAuxiliaryBackchannel> Connections => [];

    public IEnumerable<IAppHostAuxiliaryBackchannel> GetConnectionsByHash(string hash) => [];

    public string? SelectedAppHostPath { get; set; }

    public IAppHostAuxiliaryBackchannel? SelectedConnection => null;

    public Task ScanAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public IReadOnlyList<IAppHostAuxiliaryBackchannel> GetConnectionsForWorkingDirectory(DirectoryInfo workingDirectory)
    {
        // Return empty list by default (no in-scope AppHosts)
        return [];
    }
}

