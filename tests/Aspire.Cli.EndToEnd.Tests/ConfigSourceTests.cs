// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

public sealed class ConfigSourceTests(ITestOutputHelper output)
{
    [Fact]
    [CaptureWorkspaceOnFailure]
    public Task Add_UsesAllMaterializedIntegrationSources()
        => ConfigSourceTestContext.RunAsync(output, "sha1", async context =>
        {
            await context.CreateProjectAsync();
            await context.AssertSourceAppliedAsync("original");
            await context.AddAndAssertIntegrationSourcesAsync();
        });

    [Fact]
    [CaptureWorkspaceOnFailure]
    public Task Init_SolutionModeBindsAndRefreshesAppHostConfiguration()
        => ConfigSourceTestContext.RunAsync(output, "sha1", async context =>
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
        => ConfigSourceTestContext.RunAsync(output, "sha1", async context =>
        {
            await context.CreateProjectAsync();
            var configuration = await context.AssertSourceAppliedAsync("original");
            Assert.Equal("""{"custom":"keep"}""", configuration["docs"]!.ToJsonString());
        });

    [Fact]
    [CaptureWorkspaceOnFailure]
    public Task AgentInit_RegistersMaterializedSkillSources()
        => ConfigSourceTestContext.RunAsync(output, "sha1", async context =>
        {
            await context.CreateProjectAsync();
            await context.AssertSourceAppliedAsync("original");
            await context.RegisterAndAssertSkillSourcesAsync();
        });

    [Fact]
    [CaptureWorkspaceOnFailure]
    public Task New_AcceptingConfigurationSourceAppliesSettings()
        => ConfigSourceTestContext.RunAsync(output, "sha1", async context =>
        {
            await context.CreateProjectWithConsentAsync(accept: true, useOption: false);
            await context.AssertConsentResultAsync(accepted: true);
        });

    [Fact]
    [CaptureWorkspaceOnFailure]
    public Task New_DecliningConfigurationSourcePersistsOptOut()
        => ConfigSourceTestContext.RunAsync(output, "sha1", async context =>
        {
            await context.CreateProjectWithConsentAsync(accept: false, useOption: false);
            await context.AssertConsentResultAsync(accepted: false);
        });

    [Fact]
    [CaptureWorkspaceOnFailure]
    public Task New_DeclineOptionPreventsConfigurationSourceUseDuringTemplateSelection()
        => ConfigSourceTestContext.RunAsync(output, "sha1", async context =>
        {
            await context.CreateProjectWithConsentAsync(accept: false, useOption: true);
            await context.AssertConsentResultAsync(accepted: false);
        });

    [Fact]
    [CaptureWorkspaceOnFailure]
    public Task New_NonInteractiveRequiresExplicitConfigurationSourceConsent()
        => ConfigSourceTestContext.RunAsync(output, "sha1", context =>
            context.AssertNonInteractiveConsentRequiredAsync());

    [Fact]
    [CaptureWorkspaceOnFailure]
    public Task New_GitAuthenticationPromptReceivesTerminalInput()
        => ConfigSourceTestContext.RunAsync(output, "sha1", async context =>
        {
            await context.CreateProjectWithGitAuthenticationAsync();
            await context.AssertSourceAppliedAsync("original");
        });

    [Fact]
    [CaptureWorkspaceOnFailure]
    public Task Run_NotifiesWhenConfigurationSourceUpdateIsAvailable()
        => ConfigSourceTestContext.RunAsync(output, "sha1", async context =>
        {
            await context.CreateProjectAsync();
            var original = await context.AssertSourceAppliedAsync("original");
            await context.UpdateSourceAsync();
            await context.RunAndAssertUpdateNotificationAsync();
            await context.AssertConfigurationUnchangedAsync(original);
        });

    [Fact]
    [CaptureWorkspaceOnFailure]
    public Task Update_ConfigSourceWithoutBindingReportsUsefulMessage()
        => ConfigSourceTestContext.RunAsync(output, "sha1", async context =>
        {
            var original = await context.CreateUnboundProjectAsync();
            await context.AssertRefreshWithoutBindingAsync();
            await context.AssertConfigurationUnchangedAsync(original);
        });

    [Fact]
    [CaptureWorkspaceOnFailure]
    public Task Update_AcceptingConfigurationSourceUpdatesBrownfieldConfiguration()
        => ConfigSourceTestContext.RunAsync(output, "sha1", async context =>
        {
            var original = await context.CreateUnboundProjectAsync();
            await context.UpdateProjectWithSourceConsentAsync();
            await context.AssertConfigurationSourceAdoptedAsync(original);
        });

    [Theory]
    [InlineData("sha1", 40)]
    [InlineData("sha256", 64)]
    [CaptureWorkspaceOnFailure]
    public Task Update_ConfigSourceAppliesAvailableUpdate(string objectFormat, int objectIdLength)
        => ConfigSourceTestContext.RunAsync(output, objectFormat, async context =>
        {
            await context.CreateProjectAsync();
            var original = await context.AssertSourceAppliedAsync("original");
            Assert.Equal(objectIdLength, original["configSource"]!["appliedVersion"]!.GetValue<string>().Length);
            await context.UpdateSourceAsync();
            await context.RefreshConfigurationAsync(expectChanges: true);
            await context.AssertConfigurationUpdatedAsync(original);
        });

    [Fact]
    [CaptureWorkspaceOnFailure]
    public Task Update_ConfigSourceReportsAlreadyUpToDate()
        => ConfigSourceTestContext.RunAsync(output, "sha1", async context =>
        {
            await context.CreateProjectAsync();
            var original = await context.AssertSourceAppliedAsync("original");
            await context.RefreshConfigurationAsync(expectChanges: false);
            await context.AssertConfigurationUnchangedAsync(original);
        });

    [Fact]
    [CaptureWorkspaceOnFailure]
    public Task Run_NotifiesWhenAncestorConfigurationSourceUpdateIsAvailable()
        => ConfigSourceTestContext.RunAsync(output, "sha1", async context =>
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
        => ConfigSourceTestContext.RunAsync(output, "sha1", async context =>
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
