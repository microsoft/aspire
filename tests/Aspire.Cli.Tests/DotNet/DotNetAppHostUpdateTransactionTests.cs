// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Aspire.Cli.DotNet;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.TestServices;
using Aspire.Hosting.Utils;

namespace Aspire.Cli.Tests.DotNet;

public class DotNetAppHostUpdateTransactionTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Backups_RestoreOriginalBytesOrRemoveNewFiles(bool existing)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var file = new FileInfo(Path.Combine(workspace.Path, "apphost.cs"));
        byte[]? original = existing ? Encoding.UTF8.GetBytes("// original\r\n") : null;
        if (original is not null)
        {
            await File.WriteAllBytesAsync(file.FullName, original);
        }
        var backup = file.FullName + (existing ? ".aspire-backup" : ".aspire-backup-absent");
        await using (var transaction = new DotNetAppHostUpdateTransaction(new Dictionary<string, byte[]?>
        {
            [PathNormalizer.ResolveToFilesystemPath(file.FullName)] = original
        }))
        {
            await transaction.PrepareBackupsAsync([file], new TestInteractionService(), TestContext.Current.CancellationToken);
            Assert.Equal(original ?? [], await File.ReadAllBytesAsync(backup));
            await transaction.ApplyAsync([file], () => File.WriteAllTextAsync(file.FullName, "// staged"),
                TestContext.Current.CancellationToken);
        }

        Assert.Equal(original, File.Exists(file.FullName) ? await File.ReadAllBytesAsync(file.FullName) : null);
        Assert.False(File.Exists(backup));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Backups_RequireSeparateConsentToReplacePreviousRecoveryData(bool replace)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var file = new FileInfo(Path.Combine(workspace.Path, "apphost.cs"));
        await File.WriteAllTextAsync(file.FullName, "// current");
        var backup = file.FullName + ".aspire-backup";
        await File.WriteAllTextAsync(backup, "// previous recovery copy");
        var interaction = new TestInteractionService
        {
            ConfirmCallback = (_, defaultValue) =>
            {
                Assert.False(defaultValue);
                return replace;
            }
        };
        await using (var transaction = new DotNetAppHostUpdateTransaction(new Dictionary<string, byte[]?>
        {
            [PathNormalizer.ResolveToFilesystemPath(file.FullName)] = await File.ReadAllBytesAsync(file.FullName)
        }))
        {
            if (replace)
            {
                await transaction.PrepareBackupsAsync([file], interaction, TestContext.Current.CancellationToken);
                Assert.Equal("// current", await File.ReadAllTextAsync(backup));
                await transaction.ApplyAsync([file], () => File.WriteAllTextAsync(file.FullName, "// staged"),
                    TestContext.Current.CancellationToken);
            }
            else
            {
                var error = await Assert.ThrowsAsync<ProjectUpdaterException>(() =>
                    transaction.PrepareBackupsAsync([file], interaction, TestContext.Current.CancellationToken));
                Assert.Equal(UpdateCommandStrings.DryRunBackupsNotReplaced, error.Message);
                Assert.Equal("// previous recovery copy", await File.ReadAllTextAsync(backup));
            }
        }
        Assert.Equal("// current", await File.ReadAllTextAsync(file.FullName));
        Assert.Equal([UpdateCommandStrings.DryRunReplaceBackupsPrompt], interaction.BooleanPromptCalls.Select(static call => call.PromptText));
    }

    [Fact]
    public async Task Backups_PersistWhenRollbackCannotOverwriteAnExternalEdit()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var file = new FileInfo(Path.Combine(workspace.Path, "apphost.cs"));
        await File.WriteAllTextAsync(file.FullName, "// original");
        var transaction = new DotNetAppHostUpdateTransaction(new Dictionary<string, byte[]?>
        {
            [PathNormalizer.ResolveToFilesystemPath(file.FullName)] = await File.ReadAllBytesAsync(file.FullName)
        });
        await transaction.PrepareBackupsAsync([file], new TestInteractionService(), TestContext.Current.CancellationToken);
        await transaction.ApplyAsync([file], () => File.WriteAllTextAsync(file.FullName, "// staged"),
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(file.FullName, "// external");

        var failure = await Assert.ThrowsAsync<AggregateException>(() => transaction.DisposeAsync().AsTask());

        var backup = file.FullName + ".aspire-backup";
        Assert.Contains(backup, Assert.Single(failure.InnerExceptions).Message);
        Assert.Equal("// original", await File.ReadAllTextAsync(backup));
        Assert.Equal("// external", await File.ReadAllTextAsync(file.FullName));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparedApply_PreservesConcurrentConfigWriter(bool existing)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var file = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "NuGet.Config"));
        var original = existing ? Encoding.UTF8.GetBytes("<configuration />") : null;
        if (original is not null)
        {
            await File.WriteAllBytesAsync(file.FullName, original);
        }
        var candidate = new DotNetAppHostNuGetConfigMergerCandidate(
            file, original, Encoding.UTF8.GetBytes("<configuration><candidate /></configuration>"));
        await using (var transaction = new DotNetAppHostUpdateTransaction(new Dictionary<string, byte[]?>
        {
            [PathNormalizer.ResolveToFilesystemPath(file.FullName)] = original
        }))
        {
            await Assert.ThrowsAnyAsync<Exception>(() => transaction.ApplyPreparedAsync(
                file, candidate.ProposedContent,
                async writeStarting =>
                {
                    await File.WriteAllTextAsync(file.FullName, "<configuration><external /></configuration>");
                    await DotNetAppHostNuGetConfigMerger.ApplyAsync(candidate, writeStarting, TestContext.Current.CancellationToken);
                },
                TestContext.Current.CancellationToken));
        }

        Assert.Equal("<configuration><external /></configuration>", await File.ReadAllTextAsync(file.FullName));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparedApply_RollsBackSuccessfulOrPartialOwnedWrites(bool succeeds)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var file = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "NuGet.Config"));
        var original = Encoding.UTF8.GetBytes("<configuration />");
        await File.WriteAllBytesAsync(file.FullName, original);
        var proposed = Encoding.UTF8.GetBytes("<configuration><candidate /></configuration>");
        await using (var transaction = new DotNetAppHostUpdateTransaction(new Dictionary<string, byte[]?>
        {
            [PathNormalizer.ResolveToFilesystemPath(file.FullName)] = original
        }))
        {
            async Task WriteAsync(Action writeStarting)
            {
                writeStarting();
                await File.WriteAllBytesAsync(file.FullName, succeeds ? proposed : proposed[..10]);
                if (!succeeds)
                {
                    throw new IOException("Partial write.");
                }
            }

            if (succeeds)
            {
                await transaction.ApplyPreparedAsync(file, proposed, WriteAsync, TestContext.Current.CancellationToken);
            }
            else
            {
                await Assert.ThrowsAsync<IOException>(() =>
                    transaction.ApplyPreparedAsync(file, proposed, WriteAsync, TestContext.Current.CancellationToken));
            }
        }

        Assert.Equal(original, await File.ReadAllBytesAsync(file.FullName));
    }

    [Fact]
    public async Task Rollback_RefusesToOverwriteAnExternalEditOfStagedSource()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var file = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.cs"));
        var original = Encoding.UTF8.GetBytes("#:sdk Aspire.AppHost.Sdk@9.4.1");
        await File.WriteAllBytesAsync(file.FullName, original);
        var transaction = new DotNetAppHostUpdateTransaction(new Dictionary<string, byte[]?>
        {
            [PathNormalizer.ResolveToFilesystemPath(file.FullName)] = original
        });
        await transaction.ApplyAsync([file],
            () => File.WriteAllTextAsync(file.FullName, "#:sdk Aspire.AppHost.Sdk@9.4.2"),
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(file.FullName, "// External edit.");

        var failure = await Assert.ThrowsAsync<AggregateException>(() => transaction.DisposeAsync().AsTask());

        Assert.Single(failure.InnerExceptions);
        Assert.Equal("// External edit.", await File.ReadAllTextAsync(file.FullName));
        await transaction.DisposeAsync();
    }

    [Fact]
    public async Task Verify_RejectsChangesBeforeAnyCandidateEdit()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var file = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.cs"));
        var original = Encoding.UTF8.GetBytes("#:sdk Aspire.AppHost.Sdk@9.4.1");
        await File.WriteAllBytesAsync(file.FullName, original);
        await using var transaction = new DotNetAppHostUpdateTransaction(new Dictionary<string, byte[]?>
        {
            [PathNormalizer.ResolveToFilesystemPath(file.FullName)] = original
        });
        await File.WriteAllTextAsync(file.FullName, "// External edit.");
        var invoked = false;

        await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.ApplyAsync([file], () =>
        {
            invoked = true;
            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken));

        Assert.False(invoked);
        Assert.Equal("// External edit.", await File.ReadAllTextAsync(file.FullName));
    }
}
