// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Packaging;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
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
                return Task.FromResult<IEnumerable<PackageChannel>>([PackageChannel.CreateImplicitChannel(cache, new TestFeatures())]);
            }
        };
    }
}

internal static class TestExecutionContextFactory
{
    public static CliExecutionContext CreateTestContext()
    {
        return TestExecutionContextHelper.CreateExecutionContext(
            new DirectoryInfo(Path.GetTempPath()));
    }
}

internal sealed class MockAuxiliaryBackchannelMonitor : IAuxiliaryBackchannelMonitor
{
    public IEnumerable<IAppHostAuxiliaryBackchannel> Connections => [];

    public IEnumerable<IAppHostAuxiliaryBackchannel> GetConnectionsByHash(string hash) => [];

    public string? SelectedAppHostPath { get; set; }

    public IAppHostAuxiliaryBackchannel? SelectedConnection => null;

    public Task ScanAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async IAsyncEnumerable<IReadOnlyList<IAppHostAuxiliaryBackchannel>> WatchConnectionsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await ScanAsync(cancellationToken).ConfigureAwait(false);
        yield return [];
    }

    public IReadOnlyList<IAppHostAuxiliaryBackchannel> GetConnectionsForWorkingDirectory(DirectoryInfo workingDirectory)
    {
        // Return empty list by default (no in-scope AppHosts)
        return [];
    }
}

