// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

public sealed class ConfigSourceTests(ITestOutputHelper output)
{
    [Fact]
    [CaptureWorkspaceOnFailure]
    public Task Init_SolutionModeBindsAndRefreshesAppHostConfiguration()
        => ConfigSourceTestContext.RunAsync(output, async context =>
        {
            await context.InitializeSolutionAsync();
            var original = await context.AssertSourceAppliedAsync("original");
            await context.UpdateSourceAsync();
            await context.RefreshConfigurationAsync(expectChanges: true);
            await context.AssertConfigurationUpdatedAsync(original);
        });

    [Fact]
    [CaptureWorkspaceOnFailure]
    public Task New_AppliesEnvironmentConfigurationSource()
        => ConfigSourceTestContext.RunAsync(output, async context =>
        {
            await context.CreateProjectAsync();
            await context.AssertSourceAppliedAsync("original");
        });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [CaptureWorkspaceOnFailure]
    public Task New_PromptsForConfigurationSourceConsent(bool accept)
        => ConfigSourceTestContext.RunAsync(output, async context =>
        {
            await context.CreateProjectWithConsentAsync(accept);
            await context.AssertConsentResultAsync(accept);
        });

    [Fact]
    [CaptureWorkspaceOnFailure]
    public Task New_NonInteractiveRequiresExplicitConfigurationSourceConsent()
        => ConfigSourceTestContext.RunAsync(output, context =>
            context.AssertNonInteractiveConsentRequiredAsync());

    [Fact]
    [CaptureWorkspaceOnFailure]
    public Task New_GitAuthenticationPromptReceivesTerminalInput()
        => ConfigSourceTestContext.RunAsync(output, async context =>
        {
            await context.CreateProjectWithGitAuthenticationAsync();
            await context.AssertSourceAppliedAsync("original");
        });

    [Fact]
    [CaptureWorkspaceOnFailure]
    public Task Run_NotifiesWhenConfigurationSourceUpdateIsAvailable()
        => ConfigSourceTestContext.RunAsync(output, async context =>
        {
            await context.CreateProjectAsync();
            var original = await context.AssertSourceAppliedAsync("original");
            await context.UpdateSourceAsync();
            await context.RunAndAssertUpdateNotificationAsync();
            await context.AssertConfigurationUnchangedAsync(original);
        });

    [Fact]
    [CaptureWorkspaceOnFailure]
    public Task Update_ConfigSourceAppliesAvailableUpdate()
        => ConfigSourceTestContext.RunAsync(output, async context =>
        {
            await context.CreateProjectAsync();
            var original = await context.AssertSourceAppliedAsync("original");
            await context.UpdateSourceAsync();
            await context.RefreshConfigurationAsync(expectChanges: true);
            await context.AssertConfigurationUpdatedAsync(original);
        });

    [Fact]
    [CaptureWorkspaceOnFailure]
    public Task Update_ConfigSourceReportsAlreadyUpToDate()
        => ConfigSourceTestContext.RunAsync(output, async context =>
        {
            await context.CreateProjectAsync();
            var original = await context.AssertSourceAppliedAsync("original");
            await context.RefreshConfigurationAsync(expectChanges: false);
            await context.AssertConfigurationUnchangedAsync(original);
        });

    [Fact]
    [CaptureWorkspaceOnFailure]
    public Task Run_NotifiesWhenAncestorConfigurationSourceUpdateIsAvailable()
        => ConfigSourceTestContext.RunAsync(output, async context =>
        {
            await context.CreateProjectWithNestedAppHostAsync();
            var original = await context.AssertSourceAppliedAsync("original");
            await context.UseAppHostDirectoryAsync();
            await context.UpdateSourceAsync();
            await context.RunAndAssertUpdateNotificationAsync();
            await context.AssertConfigurationUnchangedAsync(original);
        });

    [Fact]
    [CaptureWorkspaceOnFailure]
    public Task Update_ConfigSourceRefreshesAncestorConfiguration()
        => ConfigSourceTestContext.RunAsync(output, async context =>
        {
            await context.CreateProjectWithNestedAppHostAsync();
            var original = await context.AssertSourceAppliedAsync("original");
            await context.UseAppHostDirectoryAsync();
            await context.UpdateSourceAsync();
            await context.RefreshConfigurationAsync(expectChanges: true);
            await context.AssertConfigurationUpdatedAsync(original);
            await context.AssertNoLocalConfigurationAsync();
        });
}
