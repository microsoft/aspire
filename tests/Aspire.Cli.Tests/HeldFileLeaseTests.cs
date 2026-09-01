// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Shared;
using Microsoft.DotNet.RemoteExecutor;

namespace Aspire.Cli.Tests;

public class HeldFileLeaseTests(ITestOutputHelper outputHelper)
{
    private const string LeaseExtension = ".lease";

    [Fact]
    public void Probe_MultipleLeasesRemainActiveUntilEveryLeaseIsDisposed()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var leaseDirectory = workspace.CreateDirectory("leases").FullName;
        var firstLease = HeldFileLease.Acquire(leaseDirectory, "test-", LeaseExtension);
        var secondLease = HeldFileLease.Acquire(leaseDirectory, "test-", LeaseExtension);

        try
        {
            Assert.NotEqual(firstLease.LeasePath, secondLease.LeasePath);
            Assert.Equal(HeldFileLeaseProbeResult.Active, HeldFileLease.Probe(leaseDirectory, LeaseExtension));

            firstLease.Dispose();

            Assert.Equal(HeldFileLeaseProbeResult.Active, HeldFileLease.Probe(leaseDirectory, LeaseExtension));
        }
        finally
        {
            firstLease.Dispose();
            secondLease.Dispose();
        }

        Assert.Equal(HeldFileLeaseProbeResult.None, HeldFileLease.Probe(leaseDirectory, LeaseExtension));
    }

    [Fact]
    public void Probe_ReclaimsOrphanLeaseAndReturnsNone()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var leaseDirectory = workspace.CreateDirectory("leases").FullName;
        var orphanPath = Path.Combine(leaseDirectory, $"orphan{LeaseExtension}");
        File.WriteAllText(orphanPath, "{}");

        var result = HeldFileLease.Probe(leaseDirectory, LeaseExtension);

        Assert.Equal(HeldFileLeaseProbeResult.None, result);
        Assert.False(File.Exists(orphanPath));
    }

    [Fact]
    public void Probe_ReturnsNoneWhenLeaseDirectoryDoesNotExist()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var leaseDirectory = Path.Combine(workspace.Path, "missing");

        Assert.Equal(HeldFileLeaseProbeResult.None, HeldFileLease.Probe(leaseDirectory, LeaseExtension));
    }

    [Fact]
    public void Probe_ReturnsActiveForLeaseHeldByAnotherProcess()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var leaseDirectory = workspace.CreateDirectory("leases").FullName;
        var readyPath = Path.Combine(workspace.Path, "ready");
        var releasePath = Path.Combine(workspace.Path, "release");

        using var result = RemoteExecutor.Invoke(static (leaseDirectory, readyPath, releasePath) =>
        {
            using var lease = HeldFileLease.Acquire(leaseDirectory, "remote-", LeaseExtension);
            File.WriteAllText(readyPath, string.Empty);

            if (!SpinWait.SpinUntil(() => File.Exists(releasePath), TimeSpan.FromSeconds(30)))
            {
                throw new TimeoutException("Timed out waiting for the parent process to release the lease holder.");
            }
        }, leaseDirectory, readyPath, releasePath);

        try
        {
            Assert.True(
                SpinWait.SpinUntil(() => File.Exists(readyPath), TimeSpan.FromSeconds(30)),
                "Timed out waiting for the child process to acquire its lease.");
            Assert.Equal(HeldFileLeaseProbeResult.Active, HeldFileLease.Probe(leaseDirectory, LeaseExtension));
        }
        finally
        {
            File.WriteAllText(releasePath, string.Empty);
        }
    }

    [Fact]
    public void Probe_ReturnsUnknownWhenLeaseDirectoryCannotBeEnumerated()
    {
        if (OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess)
        {
            Assert.Skip("Requires a non-privileged process on a platform with Unix file modes.");
            return;
        }

        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var leaseDirectory = workspace.CreateDirectory("leases").FullName;
        File.WriteAllText(Path.Combine(leaseDirectory, $"orphan{LeaseExtension}"), "{}");
        var originalMode = File.GetUnixFileMode(leaseDirectory);

        try
        {
            File.SetUnixFileMode(leaseDirectory, UnixFileMode.None);

            Assert.Equal(HeldFileLeaseProbeResult.Unknown, HeldFileLease.Probe(leaseDirectory, LeaseExtension));
        }
        finally
        {
            File.SetUnixFileMode(
                leaseDirectory,
                originalMode | UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
