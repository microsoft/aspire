// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Commands;
using Aspire.Cli.Packaging;
using Aspire.Cli.Projects;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Cli.Tests.Commands;

/// <summary>
/// Regression coverage for <c>aspire update --non-interactive</c> code paths
/// that historically reached an interactive prompt and crashed with
/// <see cref="InvalidOperationException"/> ("Interactive input is not
/// supported in this environment").
/// </summary>
public class UpdateCommandNonInteractiveTests(ITestOutputHelper outputHelper)
{
    /// <summary>
    /// Regression for #15600. Pre-fix: when hive directories existed under
    /// <c>~/.aspire/hives/</c>, the update flow called
    /// <c>PromptForSelectionAsync</c> for channel selection regardless of
    /// the host environment's interactive support. Post-fix: in a non-
    /// interactive host the implicit channel is selected silently.
    ///
    /// Repro mirrors the issue's steps: hive directory exists, no
    /// <c>--channel</c> argument, <c>--non-interactive</c> set. The fix is
    /// orthogonal to whether <c>--yes</c> is present (the channel prompt
    /// has no <c>--yes</c> binding), so we cover both forms.
    /// </summary>
    [Theory]
    [InlineData("update --non-interactive --yes")]
    [InlineData("update --channel stable --non-interactive --yes")]
    public async Task NonInteractiveUpdate_WithHives_DoesNotCrashAtChannelPrompt(string commandLine)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        // Mirror #15600 setup: leftover PR-dogfood hive on disk.
        var hivesDir = workspace.CreateDirectory(".aspire").CreateSubdirectory("hives");
        hivesDir.CreateSubdirectory("pr-99999");

        var promptInvoked = false;
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = _ => new TestProjectLocator()
            {
                UseOrFindAppHostProjectFileAsyncCallback = (_, _, _) =>
                    Task.FromResult<FileInfo?>(new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj")))
            };

            options.DotNetCliRunnerFactory = _ => new TestDotNetCliRunner();

            options.PackagingServiceFactory = _ => new TestPackagingService()
            {
                GetChannelsAsyncCallback = (_) =>
                {
                    var fakeCache = new FakeNuGetPackageCache();
                    var implicitChannel = PackageChannel.CreateImplicitChannel(fakeCache);
                    var stableChannel = PackageChannel.CreateExplicitChannel("stable", PackageChannelQuality.Stable, mappings: null, fakeCache);
                    return Task.FromResult<IEnumerable<PackageChannel>>(new[] { implicitChannel, stableChannel });
                }
            };

            options.ProjectUpdaterFactory = _ => new TestProjectUpdater()
            {
                UpdateProjectAsyncCallback = (_, _) => Task.FromResult(new ProjectUpdateResult { UpdatedApplied = true })
            };

            // Fail the test loudly if anything still tries to prompt. The
            // SUT must reach the project-update path without touching this.
            options.InteractionServiceFactory = _ => new TestInteractionService
            {
                PromptForSelectionCallback = (_, _, _, _) =>
                {
                    promptInvoked = true;
                    throw new InvalidOperationException("Interactive input is not supported in this environment.");
                },
            };
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var parsed = command.Parse(commandLine);
        var exitCode = await parsed.InvokeAsync().DefaultTimeout();

        Assert.False(promptInvoked, "Channel prompt must not be invoked in non-interactive mode (#15600).");
        Assert.Equal(ExitCodeConstants.Success, exitCode);
    }

    /// <summary>
    /// Regression for #15601. The repro from the issue
    /// (<c>aspire update --non-interactive</c> without <c>--yes</c>) now
    /// fails command-line validation early via
    /// <see cref="BaseCommand.AddNonInteractiveRequiresYesValidator"/>,
    /// surfacing a clear error message instead of crashing at the
    /// downgrade-confirmation prompt. This locks that contract in.
    /// </summary>
    [Fact]
    public async Task NonInteractiveUpdate_WithoutYes_FailsValidationEarlyInsteadOfCrashingAtConfirmPrompt()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = _ => new TestProjectLocator()
            {
                UseOrFindAppHostProjectFileAsyncCallback = (_, _, _) =>
                    Task.FromResult<FileInfo?>(new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj")))
            };

            options.InteractionServiceFactory = _ => new TestInteractionService
            {
                ConfirmCallback = (_, _) =>
                    throw new InvalidOperationException("Interactive input is not supported in this environment."),
            };
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var parsed = command.Parse("update --non-interactive");
        var exitCode = await parsed.InvokeAsync().DefaultTimeout();

        // Validator should fail this with InvalidCommand (non-zero) rather
        // than letting the command reach ProjectUpdater's ConfirmAsync.
        Assert.NotEqual(ExitCodeConstants.Success, exitCode);
    }
}
