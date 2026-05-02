// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREBROWSERLOGS001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable ASPIREINTERACTION001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable ASPIREUSERSECRETS001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests;
using Aspire.Hosting.Browsers.Resources;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Aspire.Hosting.Eventing;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using HealthStatus = Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus;

namespace Aspire.Hosting.Browsers.Tests;

[Trait("Partition", "2")]
public class BrowserLogsBuilderExtensionsTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public void WithBrowserLogs_CreatesChildResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        var web = builder.AddResource(new TestHttpResource("web"))
            .WithHttpEndpoint(targetPort: 8080)
            .WithEndpoint("http", endpoint => endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "localhost", 8080))
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = "TestHttp",
                State = new ResourceStateSnapshot(KnownResourceStates.Running, KnownResourceStateStyles.Success),
                Properties = []
            });

        web.WithBrowserLogs(browser: "chrome");

        using var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var browserLogsResource = Assert.Single(appModel.Resources.OfType<BrowserLogsResource>());
        Assert.Equal("web-browser-logs", browserLogsResource.Name);
        Assert.Equal(web.Resource.Name, browserLogsResource.ParentResource.Name);
        Assert.Equal("chrome", browserLogsResource.InitialConfiguration.Browser);
        Assert.Null(browserLogsResource.InitialConfiguration.Profile);
        Assert.Contains(browserLogsResource.Annotations.OfType<NameValidationPolicyAnnotation>(), static annotation => annotation == NameValidationPolicyAnnotation.None);

        Assert.True(browserLogsResource.TryGetAnnotationsOfType<ResourceRelationshipAnnotation>(out var relationships));
        var parentRelationship = Assert.Single(relationships, relationship => relationship.Type == "Parent");
        Assert.Equal(web.Resource.Name, parentRelationship.Resource.Name);

        var command = Assert.Single(browserLogsResource.Annotations.OfType<ResourceCommandAnnotation>(), annotation => annotation.Name == BrowserLogsBuilderExtensions.OpenTrackedBrowserCommandName);
        Assert.Equal(BrowserCommandStrings.OpenTrackedBrowserName, command.DisplayName);
        Assert.Equal(BrowserCommandStrings.OpenTrackedBrowserDescription, command.DisplayDescription);
        var configureCommand = Assert.Single(browserLogsResource.Annotations.OfType<ResourceCommandAnnotation>(), annotation => annotation.Name == BrowserLogsBuilderExtensions.ConfigureTrackedBrowserCommandName);
        Assert.Equal(BrowserCommandStrings.ConfigureTrackedBrowserName, configureCommand.DisplayName);
        Assert.Equal(BrowserCommandStrings.ConfigureTrackedBrowserDescription, configureCommand.DisplayDescription);
        var screenshotCommand = Assert.Single(browserLogsResource.Annotations.OfType<ResourceCommandAnnotation>(), annotation => annotation.Name == BrowserLogsBuilderExtensions.CaptureScreenshotCommandName);
        Assert.Equal(BrowserCommandStrings.CaptureScreenshotName, screenshotCommand.DisplayName);
        Assert.Equal(BrowserCommandStrings.CaptureScreenshotDescription, screenshotCommand.DisplayDescription);
        var inspectCommand = Assert.Single(browserLogsResource.Annotations.OfType<ResourceCommandAnnotation>(), annotation => annotation.Name == BrowserLogsBuilderExtensions.InspectBrowserCommandName);
        Assert.Equal(BrowserCommandStrings.InspectBrowserName, inspectCommand.DisplayName);
        Assert.Contains(inspectCommand.ArgumentInputs!, input => input.Name == "maxElements" && input.InputType == InputType.Number && input.Required == false);
        var clickCommand = Assert.Single(browserLogsResource.Annotations.OfType<ResourceCommandAnnotation>(), annotation => annotation.Name == BrowserLogsBuilderExtensions.ClickBrowserCommandName);
        var selectorArgument = Assert.Single(clickCommand.ArgumentInputs!, input => input.Name == "selector");
        Assert.Equal(InputType.Text, selectorArgument.InputType);
        Assert.True(selectorArgument.Required);
        var fillCommand = Assert.Single(browserLogsResource.Annotations.OfType<ResourceCommandAnnotation>(), annotation => annotation.Name == BrowserLogsBuilderExtensions.FillBrowserCommandName);
        Assert.Contains(fillCommand.ArgumentInputs!, input => input.Name == "value" && input.InputType == InputType.Text && input.Required);
        Assert.Contains(browserLogsResource.Annotations.OfType<ResourceCommandAnnotation>(), annotation => annotation.Name == BrowserLogsBuilderExtensions.NavigateBrowserCommandName);
        Assert.Contains(browserLogsResource.Annotations.OfType<ResourceCommandAnnotation>(), annotation => annotation.Name == BrowserLogsBuilderExtensions.FocusBrowserElementCommandName);
        Assert.Contains(browserLogsResource.Annotations.OfType<ResourceCommandAnnotation>(), annotation => annotation.Name == BrowserLogsBuilderExtensions.TypeBrowserTextCommandName);
        Assert.Contains(browserLogsResource.Annotations.OfType<ResourceCommandAnnotation>(), annotation => annotation.Name == BrowserLogsBuilderExtensions.PressBrowserKeyCommandName);
        Assert.Contains(browserLogsResource.Annotations.OfType<ResourceCommandAnnotation>(), annotation => annotation.Name == BrowserLogsBuilderExtensions.HoverBrowserElementCommandName);
        Assert.Contains(browserLogsResource.Annotations.OfType<ResourceCommandAnnotation>(), annotation => annotation.Name == BrowserLogsBuilderExtensions.SelectBrowserOptionCommandName);
        var scrollCommand = Assert.Single(browserLogsResource.Annotations.OfType<ResourceCommandAnnotation>(), annotation => annotation.Name == BrowserLogsBuilderExtensions.ScrollBrowserCommandName);
        Assert.Contains(scrollCommand.ArgumentInputs!, input => input.Name == "deltaY" && input.InputType == InputType.Number && !input.Required);
        Assert.Contains(browserLogsResource.Annotations.OfType<ResourceCommandAnnotation>(), annotation => annotation.Name == BrowserLogsBuilderExtensions.WaitForBrowserCommandName);
        var waitCommand = Assert.Single(browserLogsResource.Annotations.OfType<ResourceCommandAnnotation>(), annotation => annotation.Name == BrowserLogsBuilderExtensions.WaitCommandName);
        Assert.Contains(waitCommand.ArgumentInputs!, input => input.Name == "urlContains" && input.InputType == InputType.Text && !input.Required);
        Assert.Contains(waitCommand.ArgumentInputs!, input => input.Name == "loadState" && input.InputType == InputType.Choice && input.Value is null);
        Assert.Contains(waitCommand.ArgumentInputs!, input => input.Name == "function" && input.InputType == InputType.Text && !input.Required);
        var waitUrlCommand = Assert.Single(browserLogsResource.Annotations.OfType<ResourceCommandAnnotation>(), annotation => annotation.Name == BrowserLogsBuilderExtensions.WaitForBrowserUrlCommandName);
        Assert.Contains(waitUrlCommand.ArgumentInputs!, input => input.Name == "match" && input.InputType == InputType.Choice && input.Value == "contains");
        var waitLoadStateCommand = Assert.Single(browserLogsResource.Annotations.OfType<ResourceCommandAnnotation>(), annotation => annotation.Name == BrowserLogsBuilderExtensions.WaitForBrowserLoadStateCommandName);
        Assert.Contains(waitLoadStateCommand.ArgumentInputs!, input => input.Name == "state" && input.InputType == InputType.Choice && input.Value == "load");
        var waitElementStateCommand = Assert.Single(browserLogsResource.Annotations.OfType<ResourceCommandAnnotation>(), annotation => annotation.Name == BrowserLogsBuilderExtensions.WaitForBrowserElementStateCommandName);
        Assert.Contains(waitElementStateCommand.ArgumentInputs!, input => input.Name == "state" && input.InputType == InputType.Choice && input.Value == "visible");
        Assert.Contains(browserLogsResource.Annotations.OfType<ResourceCommandAnnotation>(), annotation => annotation.Name == BrowserLogsBuilderExtensions.CloseTrackedBrowserCommandName);

        var snapshot = browserLogsResource.Annotations.OfType<ResourceSnapshotAnnotation>().Single().InitialSnapshot;
        Assert.Equal(BrowserLogsBuilderExtensions.BrowserResourceType, snapshot.ResourceType);
        Assert.NotNull(snapshot.CreationTimeStamp);
        Assert.Contains(snapshot.Properties, property => property.Name == CustomResourceKnownProperties.Source && Equals(property.Value, "web"));
        Assert.Contains(snapshot.Properties, property => property.Name == BrowserLogsBuilderExtensions.BrowserPropertyName && Equals(property.Value, "chrome"));
        Assert.DoesNotContain(snapshot.Properties, property => property.Name == BrowserLogsBuilderExtensions.ProfilePropertyName);
        Assert.Contains(snapshot.Properties, property => property.Name == BrowserLogsBuilderExtensions.ActiveSessionCountPropertyName && Equals(property.Value, 0));
        Assert.Contains(snapshot.Properties, property => property.Name == BrowserLogsBuilderExtensions.ActiveSessionsPropertyName && Equals(property.Value, "None"));
        Assert.Contains(snapshot.Properties, property => property.Name == BrowserLogsBuilderExtensions.BrowserSessionsPropertyName && Equals(property.Value, "[]"));
        Assert.Contains(snapshot.Properties, property => property.Name == BrowserLogsBuilderExtensions.TotalSessionsLaunchedPropertyName && Equals(property.Value, 0));
        Assert.Empty(snapshot.HealthReports);
    }

    [Fact]
    public void WithBrowserLogs_UsesResourceSpecificConfigurationWhenArgumentsAreOmitted()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        builder.Configuration[$"{BrowserLogsBuilderExtensions.BrowserLogsConfigurationSectionName}:{BrowserLogsBuilderExtensions.BrowserConfigurationKey}"] = "msedge";
        builder.Configuration[$"{BrowserLogsBuilderExtensions.BrowserLogsConfigurationSectionName}:{BrowserLogsBuilderExtensions.ProfileConfigurationKey}"] = "Default";
        builder.Configuration[$"{BrowserLogsBuilderExtensions.BrowserLogsConfigurationSectionName}:{BrowserLogsBuilderExtensions.UserDataModeConfigurationKey}"] = nameof(BrowserUserDataMode.Shared);
        builder.Configuration[$"{BrowserLogsBuilderExtensions.BrowserLogsConfigurationSectionName}:web:{BrowserLogsBuilderExtensions.BrowserConfigurationKey}"] = "chrome";
        builder.Configuration[$"{BrowserLogsBuilderExtensions.BrowserLogsConfigurationSectionName}:web:{BrowserLogsBuilderExtensions.ProfileConfigurationKey}"] = "Profile 1";

        var web = builder.AddResource(new TestHttpResource("web"))
            .WithHttpEndpoint(targetPort: 8080)
            .WithEndpoint("http", endpoint => endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "localhost", 8080))
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = "TestHttp",
                State = new ResourceStateSnapshot(KnownResourceStates.Running, KnownResourceStateStyles.Success),
                Properties = []
            });

        web.WithBrowserLogs(browser: "chrome");

        using var app = builder.Build();
        var browserLogsResource = Assert.Single(app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<BrowserLogsResource>());

        Assert.Equal("chrome", browserLogsResource.InitialConfiguration.Browser);
        Assert.Equal("Profile 1", browserLogsResource.InitialConfiguration.Profile);

        var snapshot = browserLogsResource.Annotations.OfType<ResourceSnapshotAnnotation>().Single().InitialSnapshot;
        Assert.Contains(snapshot.Properties, property => property.Name == BrowserLogsBuilderExtensions.BrowserPropertyName && Equals(property.Value, "chrome"));
        Assert.Contains(snapshot.Properties, property => property.Name == BrowserLogsBuilderExtensions.ProfilePropertyName && Equals(property.Value, "Profile 1"));
    }

    [Fact]
    public void GetDefaultBrowser_PrefersEdgeWhenSharedModeAndEdgeIsInstalled()
    {
        var browser = BrowserConfiguration.GetDefaultBrowser(BrowserUserDataMode.Shared, browser =>
            browser switch
            {
                "chrome" => "/resolved/chrome",
                "msedge" => "/resolved/edge",
                _ => null
            });

        Assert.Equal("msedge", browser);
    }

    [Fact]
    public void GetDefaultBrowser_PrefersChromeWhenIsolatedModeAndChromeIsInstalled()
    {
        var browser = BrowserConfiguration.GetDefaultBrowser(BrowserUserDataMode.Isolated, browser =>
            browser switch
            {
                "chrome" => "/resolved/chrome",
                "msedge" => "/resolved/edge",
                _ => null
            });

        Assert.Equal("chrome", browser);
    }

    [Fact]
    public void GetDefaultBrowser_FallsBackToEdgeWhenChromeIsMissing()
    {
        var browser = BrowserConfiguration.GetDefaultBrowser(browser =>
            browser switch
            {
                "msedge" => "/resolved/edge",
                _ => null
            });

        Assert.Equal("msedge", browser);
    }

    [Fact]
    public void GetDefaultBrowser_FallsBackToChromeWhenKnownBrowsersAreMissing()
    {
        var browser = BrowserConfiguration.GetDefaultBrowser(static _ => null);

        Assert.Equal("chrome", browser);
    }

    [Fact]
    public void WithBrowserLogs_UsesDetectedDefaultBrowserWhenConfigurationIsMissing()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        var web = builder.AddResource(new TestHttpResource("web"))
            .WithHttpEndpoint(targetPort: 8080)
            .WithEndpoint("http", endpoint => endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "localhost", 8080))
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = "TestHttp",
                State = new ResourceStateSnapshot(KnownResourceStates.Running, KnownResourceStateStyles.Success),
                Properties = []
            });

        web.WithBrowserLogs();

        using var app = builder.Build();
        var browserLogsResource = Assert.Single(app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<BrowserLogsResource>());

        Assert.Equal(BrowserConfiguration.GetDefaultBrowser(ChromiumBrowserResolver.TryResolveExecutable), browserLogsResource.InitialConfiguration.Browser);
        Assert.Null(browserLogsResource.InitialConfiguration.Profile);
    }

    [Fact]
    public void WithBrowserLogs_ExplicitArgumentsOverrideConfiguration()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        builder.Configuration[$"{BrowserLogsBuilderExtensions.BrowserLogsConfigurationSectionName}:{BrowserLogsBuilderExtensions.BrowserConfigurationKey}"] = "chrome";
        builder.Configuration[$"{BrowserLogsBuilderExtensions.BrowserLogsConfigurationSectionName}:{BrowserLogsBuilderExtensions.ProfileConfigurationKey}"] = "Profile 1";
        builder.Configuration[$"{BrowserLogsBuilderExtensions.BrowserLogsConfigurationSectionName}:{BrowserLogsBuilderExtensions.UserDataModeConfigurationKey}"] = nameof(BrowserUserDataMode.Shared);

        var web = builder.AddResource(new TestHttpResource("web"))
            .WithHttpEndpoint(targetPort: 8080)
            .WithEndpoint("http", endpoint => endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "localhost", 8080))
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = "TestHttp",
                State = new ResourceStateSnapshot(KnownResourceStates.Running, KnownResourceStateStyles.Success),
                Properties = []
            });

        web.WithBrowserLogs(browser: "msedge", profile: "Default", userDataMode: BrowserUserDataMode.Shared);

        using var app = builder.Build();
        var browserLogsResource = Assert.Single(app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<BrowserLogsResource>());

        Assert.Equal("msedge", browserLogsResource.InitialConfiguration.Browser);
        Assert.Equal("Default", browserLogsResource.InitialConfiguration.Profile);
        Assert.Equal(BrowserUserDataMode.Shared, browserLogsResource.InitialConfiguration.UserDataMode);
    }

    [Fact]
    public void WithBrowserLogs_DefaultsToSharedUserDataMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        var web = builder.AddResource(new TestHttpResource("web"))
            .WithHttpEndpoint(targetPort: 8080)
            .WithEndpoint("http", endpoint => endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "localhost", 8080))
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = "TestHttp",
                State = new ResourceStateSnapshot(KnownResourceStates.Running, KnownResourceStateStyles.Success),
                Properties = []
            });

        web.WithBrowserLogs();

        using var app = builder.Build();
        var browserLogsResource = Assert.Single(app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<BrowserLogsResource>());

        Assert.Equal(BrowserUserDataMode.Shared, browserLogsResource.InitialConfiguration.UserDataMode);
        var snapshot = browserLogsResource.Annotations.OfType<ResourceSnapshotAnnotation>().Single().InitialSnapshot;
        Assert.Contains(snapshot.Properties, property => property.Name == BrowserLogsBuilderExtensions.UserDataModePropertyName && Equals(property.Value, nameof(BrowserUserDataMode.Shared)));
    }

    [Fact]
    public void WithBrowserLogs_ReadsUserDataModeFromConfiguration()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        builder.Configuration[$"{BrowserLogsBuilderExtensions.BrowserLogsConfigurationSectionName}:{BrowserLogsBuilderExtensions.UserDataModeConfigurationKey}"] = nameof(BrowserUserDataMode.Shared);

        var web = builder.AddResource(new TestHttpResource("web"))
            .WithHttpEndpoint(targetPort: 8080)
            .WithEndpoint("http", endpoint => endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "localhost", 8080))
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = "TestHttp",
                State = new ResourceStateSnapshot(KnownResourceStates.Running, KnownResourceStateStyles.Success),
                Properties = []
            });

        web.WithBrowserLogs();

        using var app = builder.Build();
        var browserLogsResource = Assert.Single(app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<BrowserLogsResource>());

        Assert.Equal(BrowserUserDataMode.Shared, browserLogsResource.InitialConfiguration.UserDataMode);
    }

    [Fact]
    public void WithBrowserLogs_RejectsProfileWhenUserDataModeIsIsolated()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        var web = builder.AddResource(new TestHttpResource("web"))
            .WithHttpEndpoint(targetPort: 8080)
            .WithEndpoint("http", endpoint => endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "localhost", 8080))
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = "TestHttp",
                State = new ResourceStateSnapshot(KnownResourceStates.Running, KnownResourceStateStyles.Success),
                Properties = []
            });

        var ex = Assert.Throws<InvalidOperationException>(
            () => web.WithBrowserLogs(profile: "Default", userDataMode: BrowserUserDataMode.Isolated));
        Assert.Contains(BrowserLogsBuilderExtensions.UserDataModeConfigurationKey, ex.Message);
    }

    [Fact]
    public void WithBrowserLogs_ExplicitUserDataModeOverridesConfiguration()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        builder.Configuration[$"{BrowserLogsBuilderExtensions.BrowserLogsConfigurationSectionName}:{BrowserLogsBuilderExtensions.UserDataModeConfigurationKey}"] = nameof(BrowserUserDataMode.Isolated);

        var web = builder.AddResource(new TestHttpResource("web"))
            .WithHttpEndpoint(targetPort: 8080)
            .WithEndpoint("http", endpoint => endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "localhost", 8080))
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = "TestHttp",
                State = new ResourceStateSnapshot(KnownResourceStates.Running, KnownResourceStateStyles.Success),
                Properties = []
            });

        web.WithBrowserLogs(userDataMode: BrowserUserDataMode.Shared);

        using var app = builder.Build();
        var browserLogsResource = Assert.Single(app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<BrowserLogsResource>());
        Assert.Equal(BrowserUserDataMode.Shared, browserLogsResource.InitialConfiguration.UserDataMode);
    }

    [Fact]
    public async Task WithBrowserLogs_CommandStartsTrackedSession()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var sessionManager = new FakeBrowserLogsSessionManager();
        builder.Services.AddSingleton<IBrowserLogsSessionManager>(sessionManager);

        var web = builder.AddResource(new TestHttpResource("web"))
            .WithHttpEndpoint(targetPort: 8080)
            .WithEndpoint("http", endpoint => endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "localhost", 8080))
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = "TestHttp",
                State = new ResourceStateSnapshot(KnownResourceStates.Running, KnownResourceStateStyles.Success),
                Properties = []
            });

        web.WithBrowserLogs(browser: "chrome");

        using var app = builder.Build();
        await app.StartAsync();

        var browserLogsResource = app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<BrowserLogsResource>().Single();
        var result = await app.ResourceCommands.ExecuteCommandAsync(browserLogsResource, BrowserLogsBuilderExtensions.OpenTrackedBrowserCommandName).DefaultTimeout();

        Assert.True(result.Success);

        var call = Assert.Single(sessionManager.Calls);
        Assert.Same(browserLogsResource, call.Resource);
        Assert.Equal(browserLogsResource.Name, call.ResourceName);
        Assert.Equal("chrome", call.Configuration.Browser);
        Assert.Null(call.Configuration.Profile);
        Assert.Equal(new Uri("http://localhost:8080", UriKind.Absolute), call.Url);
    }

    [Fact]
    public async Task WithBrowserLogs_ConfigureCommandSavesResourceScopedBrowserSettingsAndAppliesImmediately()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var interactionService = new TestInteractionService();
        var userSecretsManager = new RecordingUserSecretsManager();
        builder.Configuration[KnownConfigNames.VersionCheckDisabled] = "true";
        builder.Services.AddSingleton<IInteractionService>(interactionService);
        builder.Services.AddSingleton<IUserSecretsManager>(userSecretsManager);

        var web = builder.AddResource(new TestHttpResource("web"))
            .WithHttpEndpoint(targetPort: 8080)
            .WithEndpoint("http", endpoint => endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "localhost", 8080))
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = "TestHttp",
                State = new ResourceStateSnapshot(KnownResourceStates.Running, KnownResourceStateStyles.Success),
                Properties = []
            });

        web.WithBrowserLogs();

        using var app = builder.Build();
        await app.StartAsync();

        var browserLogsResource = app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<BrowserLogsResource>().Single();
        var commandTask = app.ResourceCommands.ExecuteCommandAsync(browserLogsResource, BrowserLogsBuilderExtensions.ConfigureTrackedBrowserCommandName);
        var interaction = await interactionService.Interactions.Reader.ReadAsync().DefaultTimeout();

        Assert.Equal(BrowserCommandStrings.ConfigureTrackedBrowserName, interaction.Title);
        Assert.Equal(BrowserCommandStrings.ConfigureTrackedBrowserPromptMessage, interaction.Message);
        Assert.Equal(BrowserCommandStrings.ConfigureTrackedBrowserSaveButton, ((InputsDialogInteractionOptions)interaction.Options!).PrimaryButtonText);
        Assert.Collection(interaction.Inputs,
            input => Assert.Equal("scope", input.Name),
            input => Assert.Equal("browser", input.Name),
            input => Assert.Equal("userDataMode", input.Name),
            input => Assert.Equal("profile", input.Name),
            input =>
            {
                Assert.Equal("saveToUserSecrets", input.Name);
                Assert.Equal("true", input.Value);
                Assert.False(input.Disabled);
                Assert.Equal(BrowserCommandStrings.ConfigureTrackedBrowserSaveToUserSecretsDescriptionConfigured, input.Description);
            });

        interaction.Inputs["scope"].Value = "resource";
        interaction.Inputs["browser"].Value = "msedge";
        interaction.Inputs["userDataMode"].Value = nameof(BrowserUserDataMode.Shared);
        interaction.Inputs["profile"].Value = "Default";
        interaction.CompletionTcs.SetResult(InteractionResult.Ok(interaction.Inputs));

        var result = await commandTask.DefaultTimeout();

        Assert.True(result.Success);
        Assert.Equal("msedge", userSecretsManager.Secrets[$"{BrowserLogsBuilderExtensions.BrowserLogsConfigurationSectionName}:web:{BrowserLogsBuilderExtensions.BrowserConfigurationKey}"]);
        Assert.Equal(nameof(BrowserUserDataMode.Shared), userSecretsManager.Secrets[$"{BrowserLogsBuilderExtensions.BrowserLogsConfigurationSectionName}:web:{BrowserLogsBuilderExtensions.UserDataModeConfigurationKey}"]);
        Assert.Equal("Default", userSecretsManager.Secrets[$"{BrowserLogsBuilderExtensions.BrowserLogsConfigurationSectionName}:web:{BrowserLogsBuilderExtensions.ProfileConfigurationKey}"]);

        var effectiveConfiguration = browserLogsResource.ResolveCurrentConfiguration(
            app.Services.GetRequiredService<IConfiguration>(),
            app.Services.GetRequiredService<BrowserLogsConfigurationStore>());
        Assert.Equal("msedge", effectiveConfiguration.Browser);
        Assert.Equal(BrowserUserDataMode.Shared, effectiveConfiguration.UserDataMode);
        Assert.Equal("Default", effectiveConfiguration.Profile);
    }

    [Fact]
    public async Task WithBrowserLogs_ConfigureCommandAppliesRuntimeSettingsWhenUserSecretsAreUnavailable()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var interactionService = new TestInteractionService();
        var userSecretsManager = new RecordingUserSecretsManager
        {
            IsAvailable = false
        };
        builder.Configuration[KnownConfigNames.VersionCheckDisabled] = "true";
        builder.Services.AddSingleton<IInteractionService>(interactionService);
        builder.Services.AddSingleton<IUserSecretsManager>(userSecretsManager);

        var web = builder.AddResource(new TestHttpResource("web"))
            .WithHttpEndpoint(targetPort: 8080)
            .WithEndpoint("http", endpoint => endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "localhost", 8080))
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = "TestHttp",
                State = new ResourceStateSnapshot(KnownResourceStates.Running, KnownResourceStateStyles.Success),
                Properties = []
            });

        web.WithBrowserLogs();

        using var app = builder.Build();
        await app.StartAsync();

        var browserLogsResource = app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<BrowserLogsResource>().Single();
        var commandTask = app.ResourceCommands.ExecuteCommandAsync(browserLogsResource, BrowserLogsBuilderExtensions.ConfigureTrackedBrowserCommandName);
        var interaction = await interactionService.Interactions.Reader.ReadAsync().DefaultTimeout();

        var saveToUserSecrets = interaction.Inputs["saveToUserSecrets"];
        Assert.True(saveToUserSecrets.Disabled);
        Assert.Null(saveToUserSecrets.Value);
        Assert.Equal(BrowserCommandStrings.ConfigureTrackedBrowserSaveToUserSecretsDescriptionNotConfigured, saveToUserSecrets.Description);

        interaction.Inputs["scope"].Value = "resource";
        interaction.Inputs["browser"].Value = "msedge";
        interaction.Inputs["userDataMode"].Value = nameof(BrowserUserDataMode.Shared);
        interaction.Inputs["profile"].Value = "Default";
        interaction.CompletionTcs.SetResult(InteractionResult.Ok(interaction.Inputs));

        var result = await commandTask.DefaultTimeout();

        Assert.True(result.Success);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, BrowserCommandStrings.ConfigureTrackedBrowserApplied, "web"), result.Message);
        Assert.Empty(userSecretsManager.Secrets);
        Assert.Empty(userSecretsManager.DeletedSecrets);

        var effectiveConfiguration = browserLogsResource.ResolveCurrentConfiguration(
            app.Services.GetRequiredService<IConfiguration>(),
            app.Services.GetRequiredService<BrowserLogsConfigurationStore>());
        Assert.Equal("msedge", effectiveConfiguration.Browser);
        Assert.Equal(BrowserUserDataMode.Shared, effectiveConfiguration.UserDataMode);
        Assert.Equal("Default", effectiveConfiguration.Profile);
    }

    [Fact]
    public async Task WithBrowserLogs_ConfigureCommandDoesNotOverrideExplicitBuilderSettings()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var interactionService = new TestInteractionService();
        var userSecretsManager = new RecordingUserSecretsManager();
        builder.Configuration[KnownConfigNames.VersionCheckDisabled] = "true";
        builder.Services.AddSingleton<IInteractionService>(interactionService);
        builder.Services.AddSingleton<IUserSecretsManager>(userSecretsManager);

        var web = builder.AddResource(new TestHttpResource("web"))
            .WithHttpEndpoint(targetPort: 8080)
            .WithEndpoint("http", endpoint => endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "localhost", 8080))
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = "TestHttp",
                State = new ResourceStateSnapshot(KnownResourceStates.Running, KnownResourceStateStyles.Success),
                Properties = []
            });

        web.WithBrowserLogs(browser: "chrome");

        using var app = builder.Build();
        await app.StartAsync();

        var browserLogsResource = app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<BrowserLogsResource>().Single();
        var commandTask = app.ResourceCommands.ExecuteCommandAsync(browserLogsResource, BrowserLogsBuilderExtensions.ConfigureTrackedBrowserCommandName);
        var interaction = await interactionService.Interactions.Reader.ReadAsync().DefaultTimeout();

        interaction.Inputs["scope"].Value = "resource";
        interaction.Inputs["browser"].Value = "msedge";
        interaction.Inputs["userDataMode"].Value = nameof(BrowserUserDataMode.Shared);
        interaction.Inputs["profile"].Value = "__aspire_browser_default__";
        interaction.CompletionTcs.SetResult(InteractionResult.Ok(interaction.Inputs));

        var result = await commandTask.DefaultTimeout();

        Assert.True(result.Success);
        Assert.Equal("msedge", userSecretsManager.Secrets[$"{BrowserLogsBuilderExtensions.BrowserLogsConfigurationSectionName}:web:{BrowserLogsBuilderExtensions.BrowserConfigurationKey}"]);

        var effectiveConfiguration = browserLogsResource.ResolveCurrentConfiguration(
            app.Services.GetRequiredService<IConfiguration>(),
            app.Services.GetRequiredService<BrowserLogsConfigurationStore>());
        Assert.Equal("chrome", effectiveConfiguration.Browser);
        Assert.Equal(BrowserUserDataMode.Shared, effectiveConfiguration.UserDataMode);
        Assert.Null(effectiveConfiguration.Profile);
    }

    [Fact]
    public async Task WithBrowserLogs_ConfigureCommandSavesGlobalSettingsAndClearsProfile()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var interactionService = new TestInteractionService();
        var userSecretsManager = new RecordingUserSecretsManager();
        builder.Configuration[KnownConfigNames.VersionCheckDisabled] = "true";
        builder.Services.AddSingleton<IInteractionService>(interactionService);
        builder.Services.AddSingleton<IUserSecretsManager>(userSecretsManager);
        builder.Configuration[$"{BrowserLogsBuilderExtensions.BrowserLogsConfigurationSectionName}:{BrowserLogsBuilderExtensions.ProfileConfigurationKey}"] = "Profile 1";

        var web = builder.AddResource(new TestHttpResource("web"))
            .WithHttpEndpoint(targetPort: 8080)
            .WithEndpoint("http", endpoint => endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "localhost", 8080))
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = "TestHttp",
                State = new ResourceStateSnapshot(KnownResourceStates.Running, KnownResourceStateStyles.Success),
                Properties = []
            });

        web.WithBrowserLogs();

        using var app = builder.Build();
        await app.StartAsync();

        var browserLogsResource = app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<BrowserLogsResource>().Single();
        var commandTask = app.ResourceCommands.ExecuteCommandAsync(browserLogsResource, BrowserLogsBuilderExtensions.ConfigureTrackedBrowserCommandName);
        var interaction = await interactionService.Interactions.Reader.ReadAsync().DefaultTimeout();

        interaction.Inputs["scope"].Value = "global";
        interaction.Inputs["browser"].Value = "chrome";
        interaction.Inputs["userDataMode"].Value = nameof(BrowserUserDataMode.Isolated);
        interaction.Inputs["profile"].Value = "__aspire_browser_default__";
        interaction.CompletionTcs.SetResult(InteractionResult.Ok(interaction.Inputs));

        var result = await commandTask.DefaultTimeout();

        Assert.True(result.Success);
        Assert.Equal("chrome", userSecretsManager.Secrets[$"{BrowserLogsBuilderExtensions.BrowserLogsConfigurationSectionName}:{BrowserLogsBuilderExtensions.BrowserConfigurationKey}"]);
        Assert.Equal(nameof(BrowserUserDataMode.Isolated), userSecretsManager.Secrets[$"{BrowserLogsBuilderExtensions.BrowserLogsConfigurationSectionName}:{BrowserLogsBuilderExtensions.UserDataModeConfigurationKey}"]);
        Assert.Contains($"{BrowserLogsBuilderExtensions.BrowserLogsConfigurationSectionName}:{BrowserLogsBuilderExtensions.ProfileConfigurationKey}", userSecretsManager.DeletedSecrets);

        var effectiveConfiguration = browserLogsResource.ResolveCurrentConfiguration(
            app.Services.GetRequiredService<IConfiguration>(),
            app.Services.GetRequiredService<BrowserLogsConfigurationStore>());
        Assert.Equal("chrome", effectiveConfiguration.Browser);
        Assert.Equal(BrowserUserDataMode.Isolated, effectiveConfiguration.UserDataMode);
        Assert.Null(effectiveConfiguration.Profile);
    }

    [Fact]
    public async Task WithBrowserLogs_ConfigureCommandDoesNotApplyRuntimeSettingsWhenUserSecretSaveFails()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var interactionService = new TestInteractionService();
        var userSecretsManager = new RecordingUserSecretsManager();
        builder.Configuration[KnownConfigNames.VersionCheckDisabled] = "true";
        builder.Configuration[$"{BrowserLogsBuilderExtensions.BrowserLogsConfigurationSectionName}:{BrowserLogsBuilderExtensions.BrowserConfigurationKey}"] = "chrome";
        builder.Configuration[$"{BrowserLogsBuilderExtensions.BrowserLogsConfigurationSectionName}:{BrowserLogsBuilderExtensions.UserDataModeConfigurationKey}"] = nameof(BrowserUserDataMode.Shared);
        builder.Services.AddSingleton<IInteractionService>(interactionService);
        builder.Services.AddSingleton<IUserSecretsManager>(userSecretsManager);

        var userDataModeKey = $"{BrowserLogsBuilderExtensions.BrowserLogsConfigurationSectionName}:web:{BrowserLogsBuilderExtensions.UserDataModeConfigurationKey}";
        userSecretsManager.FailingSetSecretNames.Add(userDataModeKey);

        var web = builder.AddResource(new TestHttpResource("web"))
            .WithHttpEndpoint(targetPort: 8080)
            .WithEndpoint("http", endpoint => endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "localhost", 8080))
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = "TestHttp",
                State = new ResourceStateSnapshot(KnownResourceStates.Running, KnownResourceStateStyles.Success),
                Properties = []
            });

        web.WithBrowserLogs();

        using var app = builder.Build();
        await app.StartAsync();

        var browserLogsResource = app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<BrowserLogsResource>().Single();
        var commandTask = app.ResourceCommands.ExecuteCommandAsync(browserLogsResource, BrowserLogsBuilderExtensions.ConfigureTrackedBrowserCommandName);
        var interaction = await interactionService.Interactions.Reader.ReadAsync().DefaultTimeout();

        interaction.Inputs["scope"].Value = "resource";
        interaction.Inputs["browser"].Value = "msedge";
        interaction.Inputs["userDataMode"].Value = nameof(BrowserUserDataMode.Isolated);
        interaction.Inputs["profile"].Value = "__aspire_browser_default__";
        interaction.CompletionTcs.SetResult(InteractionResult.Ok(interaction.Inputs));

        var result = await commandTask.DefaultTimeout();

        Assert.False(result.Success);
        Assert.Contains(userDataModeKey, result.Message, StringComparison.Ordinal);
        Assert.Equal("msedge", userSecretsManager.Secrets[$"{BrowserLogsBuilderExtensions.BrowserLogsConfigurationSectionName}:web:{BrowserLogsBuilderExtensions.BrowserConfigurationKey}"]);
        Assert.DoesNotContain(userDataModeKey, userSecretsManager.Secrets.Keys);

        var effectiveConfiguration = browserLogsResource.ResolveCurrentConfiguration(
            app.Services.GetRequiredService<IConfiguration>(),
            app.Services.GetRequiredService<BrowserLogsConfigurationStore>());
        Assert.Equal("chrome", effectiveConfiguration.Browser);
        Assert.Equal(BrowserUserDataMode.Shared, effectiveConfiguration.UserDataMode);
        Assert.Null(effectiveConfiguration.Profile);
    }

    [Fact]
    public async Task WithBrowserLogs_ConfigureCommandRefreshesAllBrowserLogsResourcesForGlobalSettings()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var interactionService = new TestInteractionService();
        var userSecretsManager = new RecordingUserSecretsManager();
        builder.Configuration[KnownConfigNames.VersionCheckDisabled] = "true";
        builder.Services.AddSingleton<IInteractionService>(interactionService);
        builder.Services.AddSingleton<IUserSecretsManager>(userSecretsManager);

        var web = builder.AddResource(new TestHttpResource("web"))
            .WithHttpEndpoint(targetPort: 8080)
            .WithEndpoint("http", endpoint => endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "localhost", 8080))
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = "TestHttp",
                State = new ResourceStateSnapshot(KnownResourceStates.Running, KnownResourceStateStyles.Success),
                Properties = []
            });

        var admin = builder.AddResource(new TestHttpResource("admin"))
            .WithHttpEndpoint(targetPort: 8081)
            .WithEndpoint("http", endpoint => endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "localhost", 8081))
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = "TestHttp",
                State = new ResourceStateSnapshot(KnownResourceStates.Running, KnownResourceStateStyles.Success),
                Properties = []
            });

        web.WithBrowserLogs();
        admin.WithBrowserLogs();

        using var app = builder.Build();
        await app.StartAsync();

        var browserLogsResources = app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<BrowserLogsResource>().ToArray();
        var webBrowserLogsResource = browserLogsResources.Single(resource => resource.ParentResource.Name == "web");
        var adminBrowserLogsResource = browserLogsResources.Single(resource => resource.ParentResource.Name == "admin");
        var commandTask = app.ResourceCommands.ExecuteCommandAsync(webBrowserLogsResource, BrowserLogsBuilderExtensions.ConfigureTrackedBrowserCommandName);
        var interaction = await interactionService.Interactions.Reader.ReadAsync().DefaultTimeout();

        interaction.Inputs["scope"].Value = "global";
        interaction.Inputs["browser"].Value = "chrome";
        interaction.Inputs["userDataMode"].Value = nameof(BrowserUserDataMode.Isolated);
        interaction.Inputs["profile"].Value = "__aspire_browser_default__";
        interaction.CompletionTcs.SetResult(InteractionResult.Ok(interaction.Inputs));

        var result = await commandTask.DefaultTimeout();

        Assert.True(result.Success);

        await app.ResourceNotifications.WaitForResourceAsync(
            adminBrowserLogsResource.Name,
            resourceEvent =>
                HasProperty(resourceEvent.Snapshot, BrowserLogsBuilderExtensions.BrowserPropertyName, "chrome") &&
                HasProperty(resourceEvent.Snapshot, BrowserLogsBuilderExtensions.UserDataModePropertyName, nameof(BrowserUserDataMode.Isolated)) &&
                DoesNotHaveProperty(resourceEvent.Snapshot, BrowserLogsBuilderExtensions.ProfilePropertyName)).DefaultTimeout();
    }

    [Fact]
    public async Task WithBrowserLogs_ConfigureCommandValidatesEffectiveConfigurationBeforeSaving()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var interactionService = new TestInteractionService();
        var userSecretsManager = new RecordingUserSecretsManager();
        builder.Configuration[KnownConfigNames.VersionCheckDisabled] = "true";
        builder.Services.AddSingleton<IInteractionService>(interactionService);
        builder.Services.AddSingleton<IUserSecretsManager>(userSecretsManager);

        var web = builder.AddResource(new TestHttpResource("web"))
            .WithHttpEndpoint(targetPort: 8080)
            .WithEndpoint("http", endpoint => endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "localhost", 8080))
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = "TestHttp",
                State = new ResourceStateSnapshot(KnownResourceStates.Running, KnownResourceStateStyles.Success),
                Properties = []
            });

        web.WithBrowserLogs(profile: "Default");

        using var app = builder.Build();
        await app.StartAsync();

        var browserLogsResource = app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<BrowserLogsResource>().Single();
        var commandTask = app.ResourceCommands.ExecuteCommandAsync(browserLogsResource, BrowserLogsBuilderExtensions.ConfigureTrackedBrowserCommandName);
        var interaction = await interactionService.Interactions.Reader.ReadAsync().DefaultTimeout();

        interaction.Inputs["scope"].Value = "resource";
        interaction.Inputs["browser"].Value = "chrome";
        interaction.Inputs["userDataMode"].Value = nameof(BrowserUserDataMode.Isolated);
        interaction.Inputs["profile"].Value = "__aspire_browser_default__";
        interaction.CompletionTcs.SetResult(InteractionResult.Ok(interaction.Inputs));

        var result = await commandTask.DefaultTimeout();

        Assert.False(result.Success);
        Assert.Contains("Profiles can only be selected", result.Message, StringComparison.Ordinal);
        Assert.Empty(userSecretsManager.Secrets);
        Assert.Empty(userSecretsManager.DeletedSecrets);

        var effectiveConfiguration = browserLogsResource.ResolveCurrentConfiguration(
            app.Services.GetRequiredService<IConfiguration>(),
            app.Services.GetRequiredService<BrowserLogsConfigurationStore>());
        Assert.Equal(BrowserUserDataMode.Shared, effectiveConfiguration.UserDataMode);
        Assert.Equal("Default", effectiveConfiguration.Profile);
    }

    [Fact]
    public async Task WithBrowserLogs_CaptureScreenshotCommandReturnsArtifactResult()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var sessionManager = new FakeBrowserLogsSessionManager
        {
            ScreenshotResult = new BrowserLogsScreenshotCaptureResult(
                "session-0002",
                "msedge",
                @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
                BrowserHostOwnership.Adopted.ToString(),
                4242,
                "target-0002",
                new Uri("https://localhost:8443/"),
                new BrowserLogsArtifact(
                    "web-browser-logs",
                    "screenshot",
                    Path.Combine(AppContext.BaseDirectory, "artifacts", "screenshot.png"),
                    "image/png",
                    1234,
                    new DateTimeOffset(2026, 4, 27, 12, 0, 0, TimeSpan.Zero)))
        };
        builder.Services.AddSingleton<IBrowserLogsSessionManager>(sessionManager);

        var web = builder.AddResource(new TestHttpResource("web"))
            .WithHttpEndpoint(targetPort: 8080)
            .WithEndpoint("http", endpoint => endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "localhost", 8080))
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = "TestHttp",
                State = new ResourceStateSnapshot(KnownResourceStates.Running, KnownResourceStateStyles.Success),
                Properties = []
            });

        web.WithBrowserLogs(browser: "chrome");

        using var app = builder.Build();
        await app.StartAsync();

        var browserLogsResource = app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<BrowserLogsResource>().Single();
        var result = await app.ResourceCommands.ExecuteCommandAsync(browserLogsResource, BrowserLogsBuilderExtensions.CaptureScreenshotCommandName).DefaultTimeout();

        Assert.True(result.Success);
        Assert.Equal("web-browser-logs", Assert.Single(sessionManager.CaptureScreenshotCalls));
        Assert.Contains("screenshot.png", result.Message);
        Assert.NotNull(result.Data);
        Assert.Equal(CommandResultFormat.Json, result.Data.Format);
        Assert.True(result.Data.DisplayImmediately);

        using var document = JsonDocument.Parse(result.Data.Value);
        Assert.Equal("web-browser-logs", document.RootElement.GetProperty("resourceName").GetString());
        Assert.Equal("session-0002", document.RootElement.GetProperty("sessionId").GetString());
        Assert.Equal("msedge", document.RootElement.GetProperty("browser").GetString());
        Assert.Equal(@"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", document.RootElement.GetProperty("browserExecutable").GetString());
        Assert.Equal("Adopted", document.RootElement.GetProperty("browserHostOwnership").GetString());
        Assert.Equal(4242, document.RootElement.GetProperty("processId").GetInt32());
        Assert.Equal("target-0002", document.RootElement.GetProperty("targetId").GetString());
        Assert.Equal("https://localhost:8443/", document.RootElement.GetProperty("targetUrl").GetString());
        Assert.EndsWith("screenshot.png", document.RootElement.GetProperty("path").GetString(), StringComparison.Ordinal);
        Assert.Equal("image/png", document.RootElement.GetProperty("mimeType").GetString());
        Assert.Equal(1234, document.RootElement.GetProperty("sizeBytes").GetInt32());
    }

    [Fact]
    public async Task WithBrowserLogs_BrowserCommandsForwardArgumentsAndReturnJson()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var sessionManager = new FakeBrowserLogsSessionManager();
        builder.Services.AddSingleton<IBrowserLogsSessionManager>(sessionManager);

        var web = builder.AddResource(new TestHttpResource("web"))
            .WithHttpEndpoint(targetPort: 8080)
            .WithEndpoint("http", endpoint => endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "localhost", 8080))
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = "TestHttp",
                State = new ResourceStateSnapshot(KnownResourceStates.Running, KnownResourceStateStyles.Success),
                Properties = []
            });

        web.WithBrowserLogs(browser: "chrome");

        using var app = builder.Build();
        await app.StartAsync();

        var browserLogsResource = app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<BrowserLogsResource>().Single();
        var result = await ExecuteBrowserCommandAsync(BrowserLogsBuilderExtensions.ClickBrowserCommandName, """{"selector":"#submit"}""");

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal(CommandResultFormat.Json, result.Data.Format);
        Assert.True(result.Data.DisplayImmediately);
        using (var resultDocument = JsonDocument.Parse(result.Data.Value))
        {
            Assert.Equal("click", resultDocument.RootElement.GetProperty("action").GetString());
        }

        await ExecuteBrowserCommandAsync(BrowserLogsBuilderExtensions.FocusBrowserElementCommandName, """{"selector":"#name"}""");
        await ExecuteBrowserCommandAsync(BrowserLogsBuilderExtensions.TypeBrowserTextCommandName, """{"selector":"#name","text":"Aspire"}""");
        await ExecuteBrowserCommandAsync(BrowserLogsBuilderExtensions.HoverBrowserElementCommandName, """{"selector":"#submit"}""");
        await ExecuteBrowserCommandAsync(BrowserLogsBuilderExtensions.ScrollBrowserCommandName, """{"selector":"#panel","deltaX":10,"deltaY":400}""");
        await ExecuteBrowserCommandAsync(BrowserLogsBuilderExtensions.WaitForBrowserUrlCommandName, """{"url":"/orders","match":"contains","timeoutMilliseconds":3000}""");
        await ExecuteBrowserCommandAsync(BrowserLogsBuilderExtensions.WaitForBrowserLoadStateCommandName, """{"state":"networkidle","timeoutMilliseconds":3000}""");
        await ExecuteBrowserCommandAsync(BrowserLogsBuilderExtensions.WaitForBrowserElementStateCommandName, """{"selector":"#submit","state":"enabled","timeoutMilliseconds":3000}""");
        await ExecuteBrowserCommandAsync(BrowserLogsBuilderExtensions.WaitCommandName, """{"urlContains":"/dashboard","timeoutMilliseconds":3000}""");
        await ExecuteBrowserCommandAsync(BrowserLogsBuilderExtensions.WaitCommandName, """{"function":"window.__appReady === true","timeoutMilliseconds":3000}""");

        Assert.Equal(
            [
                "ClickAsync:web-browser-logs:#submit",
                "FocusAsync:web-browser-logs:#name",
                "TypeAsync:web-browser-logs:#name:Aspire",
                "HoverAsync:web-browser-logs:#submit",
                "ScrollAsync:web-browser-logs:#panel:10:400",
                "WaitForUrlAsync:web-browser-logs:/orders:contains:3000",
                "WaitForLoadStateAsync:web-browser-logs:networkidle:3000",
                "WaitForElementStateAsync:web-browser-logs:#submit:enabled:3000",
                "WaitForUrlAsync:web-browser-logs:/dashboard:contains:3000",
                "WaitForFunctionAsync:web-browser-logs:window.__appReady === true:3000"
            ],
            sessionManager.BrowserCommandCalls);

        async Task<ExecuteCommandResult> ExecuteBrowserCommandAsync(string commandName, string json)
        {
            using var arguments = JsonDocument.Parse(json);
            var commandResult = await app.ResourceCommands.ExecuteCommandAsync(browserLogsResource, commandName, arguments.RootElement).DefaultTimeout();
            Assert.True(commandResult.Success);
            return commandResult;
        }
    }

    [Fact]
    public async Task WithBrowserLogs_CaptureScreenshotCommandReturnsClearFailureWhenNoSessionIsActive()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var web = builder.AddResource(new TestHttpResource("web"))
            .WithHttpEndpoint(targetPort: 8080)
            .WithEndpoint("http", endpoint => endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "localhost", 8080))
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = "TestHttp",
                State = new ResourceStateSnapshot(KnownResourceStates.Running, KnownResourceStateStyles.Success),
                Properties = []
            });

        web.WithBrowserLogs(browser: "chrome");

        using var app = builder.Build();
        await app.StartAsync();

        var browserLogsResource = app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<BrowserLogsResource>().Single();
        var result = await app.ResourceCommands.ExecuteCommandAsync(browserLogsResource, BrowserLogsBuilderExtensions.CaptureScreenshotCommandName).DefaultTimeout();

        Assert.False(result.Success);
        Assert.Equal("No active tracked browser session is available to capture.", result.Message);
    }

    [Fact]
    public async Task WithBrowserLogs_CaptureScreenshotCommandWritesPngArtifact()
    {
        var artifactDirectory = Directory.CreateTempSubdirectory();
        try
        {
            using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
            var sessionFactory = new FakeBrowserLogsRunningSessionFactory();

            builder.Services.AddSingleton<IBrowserLogsSessionManager>(sp =>
                new BrowserLogsSessionManager(
                    sp.GetRequiredService<ResourceLoggerService>(),
                    sp.GetRequiredService<ResourceNotificationService>(),
                    sp.GetRequiredService<TimeProvider>(),
                    sp.GetRequiredService<ILogger<BrowserLogsSessionManager>>(),
                    new BrowserLogsArtifactWriter(sp.GetRequiredService<TimeProvider>(), () => artifactDirectory.FullName),
                    sessionFactory));

            var web = builder.AddResource(new TestHttpResource("web"))
                .WithHttpEndpoint(targetPort: 8080)
                .WithEndpoint("http", endpoint => endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "localhost", 8080))
                .WithInitialState(new CustomResourceSnapshot
                {
                    ResourceType = "TestHttp",
                    State = new ResourceStateSnapshot(KnownResourceStates.Running, KnownResourceStateStyles.Success),
                    Properties = []
                });

            web.WithBrowserLogs(browser: "chrome");

            using var app = builder.Build();
            await app.StartAsync();

            var browserLogsResource = app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<BrowserLogsResource>().Single();
            var openResult = await app.ResourceCommands.ExecuteCommandAsync(browserLogsResource, BrowserLogsBuilderExtensions.OpenTrackedBrowserCommandName).DefaultTimeout();
            Assert.True(openResult.Success);

            var session = Assert.Single(sessionFactory.Sessions);
            session.ScreenshotBytes = [1, 2, 3, 4];

            var result = await app.ResourceCommands.ExecuteCommandAsync(browserLogsResource, BrowserLogsBuilderExtensions.CaptureScreenshotCommandName).DefaultTimeout();
            var logs = await ConsoleLoggingTestHelpers.WatchForLogsAsync(
                app.Services.GetRequiredService<ResourceLoggerService>().WatchAsync(browserLogsResource.Name),
                targetLogCount: 6).DefaultTimeout();

            Assert.True(result.Success);
            Assert.NotNull(result.Data);

            using var document = JsonDocument.Parse(result.Data.Value);
            var path = document.RootElement.GetProperty("path").GetString();

            Assert.NotNull(path);
            Assert.StartsWith(artifactDirectory.FullName, path, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(session.ScreenshotBytes, await File.ReadAllBytesAsync(path));
            Assert.Equal("session-0001", document.RootElement.GetProperty("sessionId").GetString());
            Assert.Equal("chrome", document.RootElement.GetProperty("browser").GetString());
            Assert.Equal("/fake/browser-1", document.RootElement.GetProperty("browserExecutable").GetString());
            Assert.Equal("Owned", document.RootElement.GetProperty("browserHostOwnership").GetString());
            Assert.Equal(1001, document.RootElement.GetProperty("processId").GetInt32());
            Assert.Equal("target-1", document.RootElement.GetProperty("targetId").GetString());
            Assert.Equal("http://localhost:8080/", document.RootElement.GetProperty("targetUrl").GetString());
            Assert.Equal(session.ScreenshotBytes.Length, document.RootElement.GetProperty("sizeBytes").GetInt32());
            Assert.Contains(logs, log => log.Content.Contains(path, StringComparison.Ordinal) &&
                log.Content.Contains("4 bytes", StringComparison.Ordinal) &&
                log.Content.Contains("target-1", StringComparison.Ordinal));
        }
        finally
        {
            artifactDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task WithBrowserLogs_CommandUsesLatestConfiguredSettingsAndRefreshesProperties()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var sessionFactory = new FakeBrowserLogsRunningSessionFactory();

        builder.Configuration[$"{BrowserLogsBuilderExtensions.BrowserLogsConfigurationSectionName}:{BrowserLogsBuilderExtensions.BrowserConfigurationKey}"] = "chrome";
        builder.Configuration[$"{BrowserLogsBuilderExtensions.BrowserLogsConfigurationSectionName}:{BrowserLogsBuilderExtensions.ProfileConfigurationKey}"] = "Default";
        builder.Configuration[$"{BrowserLogsBuilderExtensions.BrowserLogsConfigurationSectionName}:{BrowserLogsBuilderExtensions.UserDataModeConfigurationKey}"] = nameof(BrowserUserDataMode.Shared);

        builder.Services.AddSingleton<IBrowserLogsSessionManager>(sp =>
            new BrowserLogsSessionManager(
                sp.GetRequiredService<ResourceLoggerService>(),
                sp.GetRequiredService<ResourceNotificationService>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<BrowserLogsSessionManager>>(),
                artifactWriter: null,
                sessionFactory: sessionFactory));

        var web = builder.AddResource(new TestHttpResource("web"))
            .WithHttpEndpoint(targetPort: 8080)
            .WithEndpoint("http", endpoint => endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "localhost", 8080))
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = "TestHttp",
                State = new ResourceStateSnapshot(KnownResourceStates.Running, KnownResourceStateStyles.Success),
                Properties = []
            });

        web.WithBrowserLogs();

        using var app = builder.Build();
        await app.StartAsync();

        builder.Configuration[$"{BrowserLogsBuilderExtensions.BrowserLogsConfigurationSectionName}:{BrowserLogsBuilderExtensions.BrowserConfigurationKey}"] = "msedge";
        builder.Configuration[$"{BrowserLogsBuilderExtensions.BrowserLogsConfigurationSectionName}:{BrowserLogsBuilderExtensions.ProfileConfigurationKey}"] = null;

        var browserLogsResource = app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<BrowserLogsResource>().Single();
        var result = await app.ResourceCommands.ExecuteCommandAsync(browserLogsResource, BrowserLogsBuilderExtensions.OpenTrackedBrowserCommandName).DefaultTimeout();

        Assert.True(result.Success);

        var launchConfiguration = Assert.Single(sessionFactory.Configurations);
        Assert.Equal("msedge", launchConfiguration.Browser);
        Assert.Null(launchConfiguration.Profile);

        var runningEvent = await app.ResourceNotifications.WaitForResourceAsync(
            browserLogsResource.Name,
            resourceEvent =>
                resourceEvent.Snapshot.State?.Text == KnownResourceStates.Running &&
                HasProperty(resourceEvent.Snapshot, BrowserLogsBuilderExtensions.BrowserPropertyName, "msedge") &&
                !resourceEvent.Snapshot.Properties.Any(property => property.Name == BrowserLogsBuilderExtensions.ProfilePropertyName)).DefaultTimeout();

        var session = Assert.Single(GetBrowserSessions(runningEvent.Snapshot));
        Assert.Equal("msedge", session.Browser);
        Assert.Null(session.Profile);
    }

    [Fact]
    public async Task WithBrowserLogs_CommandRefreshesBrowserExecutablePropertyWhenRelaunchFails()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var sessionFactory = new FakeBrowserLogsRunningSessionFactory();

        builder.Configuration[$"{BrowserLogsBuilderExtensions.BrowserLogsConfigurationSectionName}:{BrowserLogsBuilderExtensions.BrowserConfigurationKey}"] = "chrome";

        builder.Services.AddSingleton<IBrowserLogsSessionManager>(sp =>
            new BrowserLogsSessionManager(
                sp.GetRequiredService<ResourceLoggerService>(),
                sp.GetRequiredService<ResourceNotificationService>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<BrowserLogsSessionManager>>(),
                artifactWriter: null,
                sessionFactory: sessionFactory));

        var web = builder.AddResource(new TestHttpResource("web"))
            .WithHttpEndpoint(targetPort: 8080)
            .WithEndpoint("http", endpoint => endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "localhost", 8080))
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = "TestHttp",
                State = new ResourceStateSnapshot(KnownResourceStates.Running, KnownResourceStateStyles.Success),
                Properties = []
            });

        web.WithBrowserLogs();

        using var app = builder.Build();
        await app.StartAsync();

        var browserLogsResource = app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<BrowserLogsResource>().Single();

        var firstResult = await app.ResourceCommands.ExecuteCommandAsync(browserLogsResource, BrowserLogsBuilderExtensions.OpenTrackedBrowserCommandName).DefaultTimeout();
        Assert.True(firstResult.Success);

        await app.ResourceNotifications.WaitForResourceAsync(
            browserLogsResource.Name,
            resourceEvent =>
                resourceEvent.Snapshot.State?.Text == KnownResourceStates.Running &&
                HasProperty(resourceEvent.Snapshot, BrowserLogsBuilderExtensions.BrowserExecutablePropertyName, "/fake/browser-1")).DefaultTimeout();

        var tempDirectory = Directory.CreateTempSubdirectory();

        try
        {
            var tempBrowserPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "tracked-browser.exe" : "tracked-browser");
            await File.WriteAllTextAsync(tempBrowserPath, string.Empty);

            builder.Configuration[$"{BrowserLogsBuilderExtensions.BrowserLogsConfigurationSectionName}:{BrowserLogsBuilderExtensions.BrowserConfigurationKey}"] = tempBrowserPath;
            sessionFactory.NextStartException = new InvalidOperationException("Launch failed.");

            var secondResult = await app.ResourceCommands.ExecuteCommandAsync(browserLogsResource, BrowserLogsBuilderExtensions.OpenTrackedBrowserCommandName).DefaultTimeout();

            Assert.False(secondResult.Success);
            Assert.Equal("Launch failed.", secondResult.Message);

            var failedEvent = await app.ResourceNotifications.WaitForResourceAsync(
                browserLogsResource.Name,
                resourceEvent =>
                    resourceEvent.Snapshot.State?.Text == KnownResourceStates.Running &&
                    HasProperty(resourceEvent.Snapshot, BrowserLogsBuilderExtensions.BrowserPropertyName, tempBrowserPath) &&
                    HasProperty(resourceEvent.Snapshot, BrowserLogsBuilderExtensions.BrowserExecutablePropertyName, tempBrowserPath) &&
                    HasProperty(resourceEvent.Snapshot, BrowserLogsBuilderExtensions.BrowserHostOwnershipPropertyName, nameof(BrowserHostOwnership.Owned)) &&
                    HasProperty(resourceEvent.Snapshot, BrowserLogsBuilderExtensions.LastErrorPropertyName, "InvalidOperationException: Launch failed.") &&
                    HasProperty(resourceEvent.Snapshot, BrowserLogsBuilderExtensions.ActiveSessionCountPropertyName, 1) &&
                    resourceEvent.Snapshot.HealthReports.Any(report =>
                        report.Name == BrowserLogsBuilderExtensions.LastErrorPropertyName &&
                        report.Status == HealthStatus.Unhealthy)).DefaultTimeout();

            Assert.Collection(
                GetBrowserSessions(failedEvent.Snapshot),
                session =>
                {
                    Assert.Equal("session-0001", session.SessionId);
                    Assert.Equal("/fake/browser-1", session.BrowserExecutable);
                });
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task WithBrowserLogs_CommandRemovesStaleBrowserExecutablePropertyWhenBrowserCannotBeResolved()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var sessionFactory = new FakeBrowserLogsRunningSessionFactory();

        builder.Configuration[$"{BrowserLogsBuilderExtensions.BrowserLogsConfigurationSectionName}:{BrowserLogsBuilderExtensions.BrowserConfigurationKey}"] = "chrome";

        builder.Services.AddSingleton<IBrowserLogsSessionManager>(sp =>
            new BrowserLogsSessionManager(
                sp.GetRequiredService<ResourceLoggerService>(),
                sp.GetRequiredService<ResourceNotificationService>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<BrowserLogsSessionManager>>(),
                artifactWriter: null,
                sessionFactory: sessionFactory));

        var web = builder.AddResource(new TestHttpResource("web"))
            .WithHttpEndpoint(targetPort: 8080)
            .WithEndpoint("http", endpoint => endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "localhost", 8080))
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = "TestHttp",
                State = new ResourceStateSnapshot(KnownResourceStates.Running, KnownResourceStateStyles.Success),
                Properties = []
            });

        web.WithBrowserLogs();

        using var app = builder.Build();
        await app.StartAsync();

        var browserLogsResource = app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<BrowserLogsResource>().Single();

        var firstResult = await app.ResourceCommands.ExecuteCommandAsync(browserLogsResource, BrowserLogsBuilderExtensions.OpenTrackedBrowserCommandName).DefaultTimeout();
        Assert.True(firstResult.Success);

        await app.ResourceNotifications.WaitForResourceAsync(
            browserLogsResource.Name,
            resourceEvent =>
                resourceEvent.Snapshot.State?.Text == KnownResourceStates.Running &&
                HasProperty(resourceEvent.Snapshot, BrowserLogsBuilderExtensions.BrowserExecutablePropertyName, "/fake/browser-1")).DefaultTimeout();

        builder.Configuration[$"{BrowserLogsBuilderExtensions.BrowserLogsConfigurationSectionName}:{BrowserLogsBuilderExtensions.BrowserConfigurationKey}"] = "missing-browser";
        sessionFactory.NextStartException = new InvalidOperationException("Launch failed.");

        var secondResult = await app.ResourceCommands.ExecuteCommandAsync(browserLogsResource, BrowserLogsBuilderExtensions.OpenTrackedBrowserCommandName).DefaultTimeout();

        Assert.False(secondResult.Success);

        var failedEvent = await app.ResourceNotifications.WaitForResourceAsync(
            browserLogsResource.Name,
            resourceEvent =>
                resourceEvent.Snapshot.State?.Text == KnownResourceStates.Running &&
                HasProperty(resourceEvent.Snapshot, BrowserLogsBuilderExtensions.BrowserPropertyName, "missing-browser") &&
                !resourceEvent.Snapshot.Properties.Any(property => property.Name == BrowserLogsBuilderExtensions.BrowserExecutablePropertyName) &&
                HasProperty(resourceEvent.Snapshot, BrowserLogsBuilderExtensions.BrowserHostOwnershipPropertyName, nameof(BrowserHostOwnership.Owned)) &&
                HasProperty(resourceEvent.Snapshot, BrowserLogsBuilderExtensions.LastErrorPropertyName, "InvalidOperationException: Launch failed.") &&
                HasProperty(resourceEvent.Snapshot, BrowserLogsBuilderExtensions.ActiveSessionCountPropertyName, 1) &&
                resourceEvent.Snapshot.HealthReports.Any(report =>
                    report.Name == BrowserLogsBuilderExtensions.LastErrorPropertyName &&
                    report.Status == HealthStatus.Unhealthy)).DefaultTimeout();

        Assert.Collection(
            GetBrowserSessions(failedEvent.Snapshot),
            session =>
            {
                Assert.Equal("session-0001", session.SessionId);
                Assert.Equal("/fake/browser-1", session.BrowserExecutable);
            });
    }

    [Fact]
    public async Task WithBrowserLogs_CommandPublishesFailureDiagnosticsWhenLaunchFailsBeforeAnySession()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var sessionFactory = new FakeBrowserLogsRunningSessionFactory
        {
            NextStartException = new InvalidOperationException("Launch failed.", new TimeoutException("CDP timed out."))
        };

        builder.Services.AddSingleton<IBrowserLogsSessionManager>(sp =>
            new BrowserLogsSessionManager(
                sp.GetRequiredService<ResourceLoggerService>(),
                sp.GetRequiredService<ResourceNotificationService>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<BrowserLogsSessionManager>>(),
                artifactWriter: null,
                sessionFactory: sessionFactory));

        var web = builder.AddResource(new TestHttpResource("web"))
            .WithHttpEndpoint(targetPort: 8080)
            .WithEndpoint("http", endpoint => endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "localhost", 8080))
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = "TestHttp",
                State = new ResourceStateSnapshot(KnownResourceStates.Running, KnownResourceStateStyles.Success),
                Properties = []
            });

        web.WithBrowserLogs(browser: "chrome");

        using var app = builder.Build();
        await app.StartAsync();

        var browserLogsResource = app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<BrowserLogsResource>().Single();
        var result = await app.ResourceCommands.ExecuteCommandAsync(browserLogsResource, BrowserLogsBuilderExtensions.OpenTrackedBrowserCommandName).DefaultTimeout();

        Assert.False(result.Success);
        Assert.Equal("Launch failed.", result.Message);

        var errorText = "InvalidOperationException: Launch failed. --> TimeoutException: CDP timed out.";
        var failedEvent = await app.ResourceNotifications.WaitForResourceAsync(
            browserLogsResource.Name,
            resourceEvent =>
                resourceEvent.Snapshot.State?.Text == KnownResourceStates.FailedToStart &&
                HasProperty(resourceEvent.Snapshot, BrowserLogsBuilderExtensions.ActiveSessionCountPropertyName, 0) &&
                HasProperty(resourceEvent.Snapshot, BrowserLogsBuilderExtensions.ActiveSessionsPropertyName, "None") &&
                HasProperty(resourceEvent.Snapshot, BrowserLogsBuilderExtensions.LastErrorPropertyName, errorText) &&
                resourceEvent.Snapshot.HealthReports.Any(report =>
                    report.Name == BrowserLogsBuilderExtensions.LastErrorPropertyName &&
                    report.Status == HealthStatus.Unhealthy &&
                    report.Description == errorText)).DefaultTimeout();

        Assert.Single(failedEvent.Snapshot.HealthReports);
        Assert.Empty(GetBrowserSessions(failedEvent.Snapshot));
    }

    [Fact]
    public async Task WithBrowserLogs_CommandClearsLastErrorAfterSuccessfulLaunch()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var sessionFactory = new FakeBrowserLogsRunningSessionFactory
        {
            NextStartException = new InvalidOperationException("Launch failed.")
        };

        builder.Services.AddSingleton<IBrowserLogsSessionManager>(sp =>
            new BrowserLogsSessionManager(
                sp.GetRequiredService<ResourceLoggerService>(),
                sp.GetRequiredService<ResourceNotificationService>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<BrowserLogsSessionManager>>(),
                artifactWriter: null,
                sessionFactory: sessionFactory));

        var web = builder.AddResource(new TestHttpResource("web"))
            .WithHttpEndpoint(targetPort: 8080)
            .WithEndpoint("http", endpoint => endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "localhost", 8080))
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = "TestHttp",
                State = new ResourceStateSnapshot(KnownResourceStates.Running, KnownResourceStateStyles.Success),
                Properties = []
            });

        web.WithBrowserLogs(browser: "chrome");

        using var app = builder.Build();
        await app.StartAsync();

        var browserLogsResource = app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<BrowserLogsResource>().Single();
        var failedResult = await app.ResourceCommands.ExecuteCommandAsync(browserLogsResource, BrowserLogsBuilderExtensions.OpenTrackedBrowserCommandName).DefaultTimeout();
        Assert.False(failedResult.Success);

        await app.ResourceNotifications.WaitForResourceAsync(
            browserLogsResource.Name,
            resourceEvent =>
                resourceEvent.Snapshot.State?.Text == KnownResourceStates.FailedToStart &&
                HasProperty(resourceEvent.Snapshot, BrowserLogsBuilderExtensions.LastErrorPropertyName, "InvalidOperationException: Launch failed.")).DefaultTimeout();

        var successfulResult = await app.ResourceCommands.ExecuteCommandAsync(browserLogsResource, BrowserLogsBuilderExtensions.OpenTrackedBrowserCommandName).DefaultTimeout();
        Assert.True(successfulResult.Success);

        var runningEvent = await app.ResourceNotifications.WaitForResourceAsync(
            browserLogsResource.Name,
            resourceEvent =>
                resourceEvent.Snapshot.State?.Text == KnownResourceStates.Running &&
                HasProperty(resourceEvent.Snapshot, BrowserLogsBuilderExtensions.ActiveSessionCountPropertyName, 1) &&
                DoesNotHaveProperty(resourceEvent.Snapshot, BrowserLogsBuilderExtensions.LastErrorPropertyName) &&
                !resourceEvent.Snapshot.HealthReports.Any(report => report.Name == BrowserLogsBuilderExtensions.LastErrorPropertyName)).DefaultTimeout();

        Assert.Collection(
            GetBrowserSessions(runningEvent.Snapshot),
            session => Assert.Equal("session-0002", session.SessionId));
    }

    [Fact]
    public async Task WithBrowserLogs_CommandSurfacesAdoptedBrowserDiagnostics()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var sessionFactory = new FakeBrowserLogsRunningSessionFactory
        {
            NextBrowserHostOwnership = BrowserHostOwnership.Adopted,
            NextProcessIdIsNull = true
        };

        builder.Services.AddSingleton<IBrowserLogsSessionManager>(sp =>
            new BrowserLogsSessionManager(
                sp.GetRequiredService<ResourceLoggerService>(),
                sp.GetRequiredService<ResourceNotificationService>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<BrowserLogsSessionManager>>(),
                artifactWriter: null,
                sessionFactory: sessionFactory));

        var web = builder.AddResource(new TestHttpResource("web"))
            .WithHttpEndpoint(targetPort: 8080)
            .WithEndpoint("http", endpoint => endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "localhost", 8080))
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = "TestHttp",
                State = new ResourceStateSnapshot(KnownResourceStates.Running, KnownResourceStateStyles.Success),
                Properties = []
            });

        web.WithBrowserLogs(browser: "msedge");

        using var app = builder.Build();
        await app.StartAsync();

        var browserLogsResource = app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<BrowserLogsResource>().Single();
        var result = await app.ResourceCommands.ExecuteCommandAsync(browserLogsResource, BrowserLogsBuilderExtensions.OpenTrackedBrowserCommandName).DefaultTimeout();
        Assert.True(result.Success);

        var runningEvent = await app.ResourceNotifications.WaitForResourceAsync(
            browserLogsResource.Name,
            resourceEvent =>
                resourceEvent.Snapshot.State?.Text == KnownResourceStates.Running &&
                HasProperty(resourceEvent.Snapshot, BrowserLogsBuilderExtensions.BrowserHostOwnershipPropertyName, nameof(BrowserHostOwnership.Adopted)) &&
                HasProperty(resourceEvent.Snapshot, BrowserLogsBuilderExtensions.ActiveSessionsPropertyName, "session-0001 (adopted browser)")).DefaultTimeout();

        var session = Assert.Single(GetBrowserSessions(runningEvent.Snapshot));
        Assert.Equal(nameof(BrowserHostOwnership.Adopted), session.BrowserHostOwnership);
        Assert.Null(session.ProcessId);
    }

    [Fact]
    public async Task WithBrowserLogs_CommandFailsWhenEndpointIsMissing()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var sessionManager = new FakeBrowserLogsSessionManager();
        builder.Services.AddSingleton<IBrowserLogsSessionManager>(sessionManager);

        var web = builder.AddResource(new TestHttpResource("web"))
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = "TestHttp",
                State = new ResourceStateSnapshot(KnownResourceStates.Running, KnownResourceStateStyles.Success),
                Properties = []
            });

        web.WithBrowserLogs();

        using var app = builder.Build();
        await app.StartAsync();

        var browserLogsResource = app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<BrowserLogsResource>().Single();
        var result = await app.ResourceCommands.ExecuteCommandAsync(browserLogsResource, BrowserLogsBuilderExtensions.OpenTrackedBrowserCommandName).DefaultTimeout();

        Assert.False(result.Success);
        Assert.Equal("Resource 'web' does not have an HTTP or HTTPS endpoint. Browser logs require an endpoint to navigate to.", result.Message);
        Assert.Empty(sessionManager.Calls);
    }

    [Fact]
    public async Task WithBrowserLogs_CommandBecomesEnabledWhenParentReady()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var web = builder.AddResource(new TestHttpResource("web"))
            .WithHttpEndpoint(targetPort: 8080)
            .WithEndpoint("http", endpoint => endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "localhost", 8080))
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = "TestHttp",
                State = KnownResourceStates.NotStarted,
                Properties = []
            });

        web.WithBrowserLogs(browser: "chrome");

        using var app = builder.Build();
        await app.StartAsync();

        var browserLogsResource = app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<BrowserLogsResource>().Single();
        var initialEvent = await app.ResourceNotifications.WaitForResourceAsync(
            browserLogsResource.Name,
            resourceEvent => resourceEvent.Snapshot.Commands.Any(command =>
                command.Name == BrowserLogsBuilderExtensions.OpenTrackedBrowserCommandName &&
                command.State == ResourceCommandState.Disabled)).DefaultTimeout();

        Assert.Equal(ResourceCommandState.Disabled, initialEvent.Snapshot.Commands.Single(command => command.Name == BrowserLogsBuilderExtensions.OpenTrackedBrowserCommandName).State);

        await app.ResourceNotifications.PublishUpdateAsync(web.Resource, snapshot => snapshot with
        {
            State = KnownResourceStates.Running
        }).DefaultTimeout();

        var eventing = app.Services.GetRequiredService<IDistributedApplicationEventing>();
        await eventing.PublishAsync(new ResourceReadyEvent(web.Resource, app.Services)).DefaultTimeout();

        var enabledEvent = await app.ResourceNotifications.WaitForResourceAsync(
            browserLogsResource.Name,
            resourceEvent => resourceEvent.Snapshot.Commands.Any(command =>
                command.Name == BrowserLogsBuilderExtensions.OpenTrackedBrowserCommandName &&
                command.State == ResourceCommandState.Enabled)).DefaultTimeout();

        Assert.Equal(ResourceCommandState.Enabled, enabledEvent.Snapshot.Commands.Single(command => command.Name == BrowserLogsBuilderExtensions.OpenTrackedBrowserCommandName).State);
    }

    [Fact]
    public async Task WithBrowserLogs_CommandTracksMultipleSessionsWithUniqueIds()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var sessionFactory = new FakeBrowserLogsRunningSessionFactory();

        builder.Services.AddSingleton<IBrowserLogsSessionManager>(sp =>
            new BrowserLogsSessionManager(
                sp.GetRequiredService<ResourceLoggerService>(),
                sp.GetRequiredService<ResourceNotificationService>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<BrowserLogsSessionManager>>(),
                artifactWriter: null,
                sessionFactory: sessionFactory));

        var web = builder.AddResource(new TestHttpResource("web"))
            .WithHttpEndpoint(targetPort: 8080)
            .WithEndpoint("http", endpoint => endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "localhost", 8080))
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = "TestHttp",
                State = new ResourceStateSnapshot(KnownResourceStates.Running, KnownResourceStateStyles.Success),
                Properties = []
            });

        web.WithBrowserLogs(browser: "chrome", profile: "Default", userDataMode: BrowserUserDataMode.Shared);

        using var app = builder.Build();
        await app.StartAsync();

        var browserLogsResource = app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<BrowserLogsResource>().Single();

        var firstResult = await app.ResourceCommands.ExecuteCommandAsync(browserLogsResource, BrowserLogsBuilderExtensions.OpenTrackedBrowserCommandName).DefaultTimeout();
        Assert.True(firstResult.Success);

        var firstSession = Assert.Single(sessionFactory.Sessions);
        Assert.Equal("session-0001", firstSession.SessionId);

        var firstRunningEvent = await app.ResourceNotifications.WaitForResourceAsync(
            browserLogsResource.Name,
            resourceEvent =>
                resourceEvent.Snapshot.State?.Text == KnownResourceStates.Running &&
                HasProperty(resourceEvent.Snapshot, BrowserLogsBuilderExtensions.ActiveSessionCountPropertyName, 1) &&
                HasProperty(resourceEvent.Snapshot, BrowserLogsBuilderExtensions.ActiveSessionsPropertyName, "session-0001 (PID 1001)") &&
                HasProperty(resourceEvent.Snapshot, BrowserLogsBuilderExtensions.TotalSessionsLaunchedPropertyName, 1) &&
                HasProperty(resourceEvent.Snapshot, BrowserLogsBuilderExtensions.LastSessionPropertyName, "session-0001") &&
                HasProperty(resourceEvent.Snapshot, BrowserLogsBuilderExtensions.BrowserExecutablePropertyName, "/fake/browser-1") &&
                resourceEvent.Snapshot.HealthReports.Any(report => report.Name == "session-0001" && report.Status == HealthStatus.Healthy)).DefaultTimeout();

        Assert.Single(firstRunningEvent.Snapshot.HealthReports);
        Assert.Collection(
            GetBrowserSessions(firstRunningEvent.Snapshot),
            session =>
            {
                Assert.Equal("session-0001", session.SessionId);
                Assert.Equal("chrome", session.Browser);
                Assert.Equal("/fake/browser-1", session.BrowserExecutable);
                Assert.Equal(nameof(BrowserHostOwnership.Owned), session.BrowserHostOwnership);
                Assert.Equal(1001, session.ProcessId);
                Assert.Equal("Default", session.Profile);
                Assert.Equal("http://localhost:8080/", session.TargetUrl);
                Assert.Equal("ws://127.0.0.1:9001/devtools/browser/browser-1", session.CdpEndpoint);
                Assert.Equal("ws://127.0.0.1:9001/devtools/page/target-1", session.PageCdpEndpoint);
                Assert.Equal("target-1", session.TargetId);
            });
        Assert.Equal(0, firstSession.StopCallCount);

        var secondResult = await app.ResourceCommands.ExecuteCommandAsync(browserLogsResource, BrowserLogsBuilderExtensions.OpenTrackedBrowserCommandName).DefaultTimeout();
        Assert.True(secondResult.Success);

        Assert.Equal(2, sessionFactory.Sessions.Count);
        var secondSession = sessionFactory.Sessions[1];
        Assert.Equal("session-0002", secondSession.SessionId);

        var secondRunningEvent = await app.ResourceNotifications.WaitForResourceAsync(
            browserLogsResource.Name,
            resourceEvent =>
                resourceEvent.Snapshot.State?.Text == KnownResourceStates.Running &&
                HasProperty(resourceEvent.Snapshot, BrowserLogsBuilderExtensions.ActiveSessionCountPropertyName, 2) &&
                HasProperty(resourceEvent.Snapshot, BrowserLogsBuilderExtensions.ActiveSessionsPropertyName, "session-0001 (PID 1001), session-0002 (PID 1002)") &&
                HasProperty(resourceEvent.Snapshot, BrowserLogsBuilderExtensions.TotalSessionsLaunchedPropertyName, 2) &&
                HasProperty(resourceEvent.Snapshot, BrowserLogsBuilderExtensions.LastSessionPropertyName, "session-0002") &&
                HasProperty(resourceEvent.Snapshot, BrowserLogsBuilderExtensions.BrowserExecutablePropertyName, "/fake/browser-2") &&
                resourceEvent.Snapshot.HealthReports.Any(report => report.Name == "session-0001" && report.Status == HealthStatus.Healthy) &&
                resourceEvent.Snapshot.HealthReports.Any(report => report.Name == "session-0002" && report.Status == HealthStatus.Healthy)).DefaultTimeout();

        Assert.Equal(2, secondRunningEvent.Snapshot.HealthReports.Length);
        Assert.Collection(
            GetBrowserSessions(secondRunningEvent.Snapshot),
            session =>
            {
                Assert.Equal("session-0001", session.SessionId);
                Assert.Equal("ws://127.0.0.1:9001/devtools/browser/browser-1", session.CdpEndpoint);
                Assert.Equal("ws://127.0.0.1:9001/devtools/page/target-1", session.PageCdpEndpoint);
            },
            session =>
            {
                Assert.Equal("session-0002", session.SessionId);
                Assert.Equal("ws://127.0.0.1:9002/devtools/browser/browser-2", session.CdpEndpoint);
                Assert.Equal("ws://127.0.0.1:9002/devtools/page/target-2", session.PageCdpEndpoint);
                Assert.Equal("target-2", session.TargetId);
            });
        Assert.Equal(0, firstSession.StopCallCount);

        await firstSession.CompleteAsync(exitCode: 0);

        var firstCompletedEvent = await app.ResourceNotifications.WaitForResourceAsync(
            browserLogsResource.Name,
            resourceEvent =>
                resourceEvent.Snapshot.State?.Text == KnownResourceStates.Running &&
                HasProperty(resourceEvent.Snapshot, BrowserLogsBuilderExtensions.ActiveSessionCountPropertyName, 1) &&
                HasProperty(resourceEvent.Snapshot, BrowserLogsBuilderExtensions.ActiveSessionsPropertyName, "session-0002 (PID 1002)") &&
                resourceEvent.Snapshot.HealthReports.Length == 1 &&
                resourceEvent.Snapshot.HealthReports[0].Name == "session-0002").DefaultTimeout();

        Assert.Equal("session-0002", firstCompletedEvent.Snapshot.HealthReports[0].Name);
        Assert.Collection(
            GetBrowserSessions(firstCompletedEvent.Snapshot),
            session => Assert.Equal("session-0002", session.SessionId));

        await secondSession.CompleteAsync(exitCode: 0);

        var allCompletedEvent = await app.ResourceNotifications.WaitForResourceAsync(
            browserLogsResource.Name,
            resourceEvent =>
                resourceEvent.Snapshot.State?.Text == KnownResourceStates.Finished &&
                HasProperty(resourceEvent.Snapshot, BrowserLogsBuilderExtensions.ActiveSessionCountPropertyName, 0) &&
                HasProperty(resourceEvent.Snapshot, BrowserLogsBuilderExtensions.ActiveSessionsPropertyName, "None") &&
                HasProperty(resourceEvent.Snapshot, BrowserLogsBuilderExtensions.TotalSessionsLaunchedPropertyName, 2) &&
                resourceEvent.Snapshot.HealthReports.IsEmpty).DefaultTimeout();

        Assert.Equal(KnownResourceStates.Finished, allCompletedEvent.Snapshot.State?.Text);
        Assert.Empty(GetBrowserSessions(allCompletedEvent.Snapshot));
    }

    [Fact]
    public async Task WithBrowserLogs_PreservesLastErrorWhenOneOfMultipleSessionsFails()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var sessionFactory = new FakeBrowserLogsRunningSessionFactory();

        builder.Services.AddSingleton<IBrowserLogsSessionManager>(sp =>
            new BrowserLogsSessionManager(
                sp.GetRequiredService<ResourceLoggerService>(),
                sp.GetRequiredService<ResourceNotificationService>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<BrowserLogsSessionManager>>(),
                artifactWriter: null,
                sessionFactory: sessionFactory));

        var web = builder.AddResource(new TestHttpResource("web"))
            .WithHttpEndpoint(targetPort: 8080)
            .WithEndpoint("http", endpoint => endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "localhost", 8080))
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = "TestHttp",
                State = new ResourceStateSnapshot(KnownResourceStates.Running, KnownResourceStateStyles.Success),
                Properties = []
            });

        web.WithBrowserLogs(browser: "chrome");

        using var app = builder.Build();
        await app.StartAsync();

        var browserLogsResource = app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<BrowserLogsResource>().Single();

        Assert.True((await app.ResourceCommands.ExecuteCommandAsync(browserLogsResource, BrowserLogsBuilderExtensions.OpenTrackedBrowserCommandName).DefaultTimeout()).Success);
        Assert.True((await app.ResourceCommands.ExecuteCommandAsync(browserLogsResource, BrowserLogsBuilderExtensions.OpenTrackedBrowserCommandName).DefaultTimeout()).Success);

        var firstSession = sessionFactory.Sessions[0];
        var secondSession = sessionFactory.Sessions[1];
        await firstSession.CompleteAsync(exitCode: 0, error: new InvalidOperationException("Target crashed."));

        var errorText = "InvalidOperationException: Target crashed.";
        await app.ResourceNotifications.WaitForResourceAsync(
            browserLogsResource.Name,
            resourceEvent =>
                resourceEvent.Snapshot.State?.Text == KnownResourceStates.Running &&
                HasProperty(resourceEvent.Snapshot, BrowserLogsBuilderExtensions.ActiveSessionCountPropertyName, 1) &&
                HasProperty(resourceEvent.Snapshot, BrowserLogsBuilderExtensions.LastErrorPropertyName, errorText) &&
                resourceEvent.Snapshot.HealthReports.Any(report =>
                    report.Name == "session-0002" &&
                    report.Status == HealthStatus.Healthy) &&
                resourceEvent.Snapshot.HealthReports.Any(report =>
                    report.Name == BrowserLogsBuilderExtensions.LastErrorPropertyName &&
                    report.Status == HealthStatus.Unhealthy &&
                    report.Description == errorText)).DefaultTimeout();

        await secondSession.CompleteAsync(exitCode: 0);

        await app.ResourceNotifications.WaitForResourceAsync(
            browserLogsResource.Name,
            resourceEvent =>
                resourceEvent.Snapshot.State?.Text == KnownResourceStates.Exited &&
                HasProperty(resourceEvent.Snapshot, BrowserLogsBuilderExtensions.ActiveSessionCountPropertyName, 0) &&
                HasProperty(resourceEvent.Snapshot, BrowserLogsBuilderExtensions.LastErrorPropertyName, errorText) &&
                resourceEvent.Snapshot.HealthReports.Any(report =>
                    report.Name == BrowserLogsBuilderExtensions.LastErrorPropertyName &&
                    report.Status == HealthStatus.Unhealthy &&
                    report.Description == errorText)).DefaultTimeout();
    }

    [Fact]
    public async Task WithBrowserLogs_DisposeWaitsForCompletionObservers()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var sessionFactory = new FakeBrowserLogsRunningSessionFactory();

        builder.Services.AddSingleton<IBrowserLogsSessionManager>(sp =>
            new BrowserLogsSessionManager(
                sp.GetRequiredService<ResourceLoggerService>(),
                sp.GetRequiredService<ResourceNotificationService>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<BrowserLogsSessionManager>>(),
                artifactWriter: null,
                sessionFactory: sessionFactory));

        var web = builder.AddResource(new TestHttpResource("web"))
            .WithHttpEndpoint(targetPort: 8080)
            .WithEndpoint("http", endpoint => endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "localhost", 8080))
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = "TestHttp",
                State = new ResourceStateSnapshot(KnownResourceStates.Running, KnownResourceStateStyles.Success),
                Properties = []
            });

        web.WithBrowserLogs(browser: "chrome");

        var app = builder.Build();
        var disposed = false;

        try
        {
            await app.StartAsync();

            var browserLogsResource = app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<BrowserLogsResource>().Single();
            var result = await app.ResourceCommands.ExecuteCommandAsync(browserLogsResource, BrowserLogsBuilderExtensions.OpenTrackedBrowserCommandName).DefaultTimeout();
            Assert.True(result.Success);

            var session = Assert.Single(sessionFactory.Sessions);
            session.PauseCompletionObserver();

            var disposeTask = app.DisposeAsync().AsTask();

            await session.CompletionObserverStarted.DefaultTimeout();
            Assert.False(disposeTask.IsCompleted);

            session.ResumeCompletionObserver();
            await disposeTask.DefaultTimeout();
            disposed = true;
        }
        finally
        {
            if (!disposed)
            {
                await app.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task BrowserEventLogger_LogsSuccessfulNetworkRequests()
    {
        var resourceLoggerService = ConsoleLoggingTestHelpers.GetResourceLoggerService();
        var resourceLogger = resourceLoggerService.GetLogger("web-browser-logs");
        var eventLogger = new BrowserEventLogger("session-0001", resourceLogger);
        var logs = await CaptureLogsAsync(resourceLoggerService, "web-browser-logs", () =>
        {
            eventLogger.HandleEvent(ParseProtocolEvent("""
                {
                  "method": "Network.requestWillBeSent",
                  "sessionId": "target-session-1",
                  "params": {
                    "requestId": "request-1",
                    "timestamp": 1.5,
                    "type": "Fetch",
                    "request": {
                      "url": "https://example.test/api/todos",
                      "method": "GET"
                    }
                  }
                }
                """));
            eventLogger.HandleEvent(ParseProtocolEvent("""
                {
                  "method": "Network.responseReceived",
                  "sessionId": "target-session-1",
                  "params": {
                    "requestId": "request-1",
                    "timestamp": 1.6,
                    "type": "Fetch",
                    "response": {
                      "url": "https://example.test/api/todos",
                      "status": 200,
                      "statusText": "OK",
                      "fromDiskCache": false,
                      "fromServiceWorker": false
                    }
                  }
                }
                """));
            eventLogger.HandleEvent(ParseProtocolEvent("""
                {
                  "method": "Network.loadingFinished",
                  "sessionId": "target-session-1",
                  "params": {
                    "requestId": "request-1",
                    "timestamp": 1.75,
                    "encodedDataLength": 1024
                  }
                }
                """));
        });
        var log = Assert.Single(logs);

        Assert.Equal("2000-12-29T20:59:59.0000000Z [session-0001] [network.fetch] GET https://example.test/api/todos -> 200 OK (250 ms, 1024 B)", log.Content);
    }

    [Fact]
    public async Task BrowserEventLogger_LogsFailedNetworkRequests()
    {
        var resourceLoggerService = ConsoleLoggingTestHelpers.GetResourceLoggerService();
        var resourceLogger = resourceLoggerService.GetLogger("web-browser-logs");
        var eventLogger = new BrowserEventLogger("session-0002", resourceLogger);
        var logs = await CaptureLogsAsync(resourceLoggerService, "web-browser-logs", () =>
        {
            eventLogger.HandleEvent(ParseProtocolEvent("""
                {
                  "method": "Network.requestWillBeSent",
                  "sessionId": "target-session-2",
                  "params": {
                    "requestId": "request-2",
                    "timestamp": 5.0,
                    "type": "Document",
                    "request": {
                      "url": "https://127.0.0.1:1/browser-network-failure",
                      "method": "GET"
                    }
                  }
                }
                """));
            eventLogger.HandleEvent(ParseProtocolEvent("""
                {
                  "method": "Network.loadingFailed",
                  "sessionId": "target-session-2",
                  "params": {
                    "requestId": "request-2",
                    "timestamp": 5.15,
                    "errorText": "net::ERR_CONNECTION_REFUSED",
                    "canceled": false
                  }
                }
                """));
        });
        var log = Assert.Single(logs);

        Assert.Equal("2000-12-29T20:59:59.0000000Z [session-0002] [network.document] GET https://127.0.0.1:1/browser-network-failure failed: net::ERR_CONNECTION_REFUSED (150 ms)", log.Content);
    }

    private sealed class TestHttpResource(string name) : Resource(name), IResourceWithEndpoints;

    private static bool HasProperty(CustomResourceSnapshot snapshot, string name, object expectedValue) =>
        snapshot.Properties.Any(property => property.Name == name && Equals(property.Value, expectedValue));

    private static bool DoesNotHaveProperty(CustomResourceSnapshot snapshot, string name) =>
        !snapshot.Properties.Any(property => property.Name == name);

    private static IReadOnlyList<BrowserSessionPropertyValue> GetBrowserSessions(CustomResourceSnapshot snapshot)
    {
        var property = snapshot.Properties.Single(property => property.Name == BrowserLogsBuilderExtensions.BrowserSessionsPropertyName);
        var value = Assert.IsType<string>(property.Value);
        return JsonSerializer.Deserialize<List<BrowserSessionPropertyValue>>(value, BrowserSessionPropertyJsonOptions)
            ?? throw new InvalidOperationException("Expected browser session property JSON.");
    }

    private static BrowserLogsCdpProtocolEvent ParseProtocolEvent(string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        return BrowserLogsCdpProtocol.ParseEvent(BrowserLogsCdpProtocol.ParseMessageHeader(payload), payload)
            ?? throw new InvalidOperationException("Expected a browser protocol event frame.");
    }

    private static Task<IReadOnlyList<LogLine>> CaptureLogsAsync(ResourceLoggerService resourceLoggerService, string resourceName, Action writeLogs) =>
        ConsoleLoggingTestHelpers.CaptureLogsAsync(resourceLoggerService, resourceName, targetLogCount: 1, writeLogs);

    private sealed class FakeBrowserLogsSessionManager : IBrowserLogsSessionManager
    {
        public List<SessionStartCall> Calls { get; } = [];

        public List<string> CaptureScreenshotCalls { get; } = [];

        public List<string> BrowserCommandCalls { get; } = [];

        public BrowserLogsScreenshotCaptureResult ScreenshotResult { get; set; } = new(
            "session-0001",
            "chrome",
            "/fake/browser",
            BrowserHostOwnership.Owned.ToString(),
            1001,
            "target-1",
            new Uri("https://localhost:5001/"),
            new BrowserLogsArtifact(
                "web-browser-logs",
                "screenshot",
                Path.Combine(AppContext.BaseDirectory, "screenshot.png"),
                "image/png",
                0,
                DateTimeOffset.UnixEpoch));

        public Task StartSessionAsync(BrowserLogsResource resource, BrowserConfiguration configuration, string resourceName, Uri url, CancellationToken cancellationToken)
        {
            Calls.Add(new SessionStartCall(resource, configuration, resourceName, url));
            return Task.CompletedTask;
        }

        public Task<BrowserLogsScreenshotCaptureResult> CaptureScreenshotAsync(string resourceName, CancellationToken cancellationToken)
        {
            CaptureScreenshotCalls.Add(resourceName);
            return Task.FromResult(ScreenshotResult);
        }

        public Task<string> GetPageSnapshotAsync(string resourceName, int maxElements, int maxTextLength, CancellationToken cancellationToken)
        {
            BrowserCommandCalls.Add($"{nameof(GetPageSnapshotAsync)}:{resourceName}:{maxElements}:{maxTextLength}");
            return Task.FromResult("""{"action":"snapshot"}""");
        }

        public Task<string> NavigateAsync(BrowserLogsResource resource, string resourceName, Uri url, CancellationToken cancellationToken)
        {
            BrowserCommandCalls.Add($"{nameof(NavigateAsync)}:{resourceName}:{url}");
            return Task.FromResult("""{"action":"navigate"}""");
        }

        public Task<string> ClickAsync(string resourceName, string selector, CancellationToken cancellationToken)
        {
            BrowserCommandCalls.Add($"{nameof(ClickAsync)}:{resourceName}:{selector}");
            return Task.FromResult("""{"action":"click"}""");
        }

        public Task<string> FillAsync(string resourceName, string selector, string value, CancellationToken cancellationToken)
        {
            BrowserCommandCalls.Add($"{nameof(FillAsync)}:{resourceName}:{selector}:{value}");
            return Task.FromResult("""{"action":"fill"}""");
        }

        public Task<string> FocusAsync(string resourceName, string selector, CancellationToken cancellationToken)
        {
            BrowserCommandCalls.Add($"{nameof(FocusAsync)}:{resourceName}:{selector}");
            return Task.FromResult("""{"action":"focus"}""");
        }

        public Task<string> TypeAsync(string resourceName, string selector, string text, CancellationToken cancellationToken)
        {
            BrowserCommandCalls.Add($"{nameof(TypeAsync)}:{resourceName}:{selector}:{text}");
            return Task.FromResult("""{"action":"type"}""");
        }

        public Task<string> PressAsync(string resourceName, string? selector, string key, CancellationToken cancellationToken)
        {
            BrowserCommandCalls.Add($"{nameof(PressAsync)}:{resourceName}:{selector}:{key}");
            return Task.FromResult("""{"action":"press"}""");
        }

        public Task<string> HoverAsync(string resourceName, string selector, CancellationToken cancellationToken)
        {
            BrowserCommandCalls.Add($"{nameof(HoverAsync)}:{resourceName}:{selector}");
            return Task.FromResult("""{"action":"hover"}""");
        }

        public Task<string> SelectAsync(string resourceName, string selector, string value, CancellationToken cancellationToken)
        {
            BrowserCommandCalls.Add($"{nameof(SelectAsync)}:{resourceName}:{selector}:{value}");
            return Task.FromResult("""{"action":"select"}""");
        }

        public Task<string> ScrollAsync(string resourceName, string? selector, int deltaX, int deltaY, CancellationToken cancellationToken)
        {
            BrowserCommandCalls.Add($"{nameof(ScrollAsync)}:{resourceName}:{selector}:{deltaX}:{deltaY}");
            return Task.FromResult("""{"action":"scroll"}""");
        }

        public Task<string> WaitForAsync(string resourceName, string? selector, string? text, int timeoutMilliseconds, CancellationToken cancellationToken)
        {
            BrowserCommandCalls.Add($"{nameof(WaitForAsync)}:{resourceName}:{selector}:{text}:{timeoutMilliseconds}");
            return Task.FromResult("""{"action":"wait-for"}""");
        }

        public Task<string> WaitForUrlAsync(string resourceName, string url, string match, int timeoutMilliseconds, CancellationToken cancellationToken)
        {
            BrowserCommandCalls.Add($"{nameof(WaitForUrlAsync)}:{resourceName}:{url}:{match}:{timeoutMilliseconds}");
            return Task.FromResult("""{"action":"wait-for-url"}""");
        }

        public Task<string> WaitForLoadStateAsync(string resourceName, string state, int timeoutMilliseconds, CancellationToken cancellationToken)
        {
            BrowserCommandCalls.Add($"{nameof(WaitForLoadStateAsync)}:{resourceName}:{state}:{timeoutMilliseconds}");
            return Task.FromResult("""{"action":"wait-for-load-state"}""");
        }

        public Task<string> WaitForElementStateAsync(string resourceName, string selector, string state, int timeoutMilliseconds, CancellationToken cancellationToken)
        {
            BrowserCommandCalls.Add($"{nameof(WaitForElementStateAsync)}:{resourceName}:{selector}:{state}:{timeoutMilliseconds}");
            return Task.FromResult("""{"action":"wait-for-element-state"}""");
        }

        public Task<string> WaitForFunctionAsync(string resourceName, string function, int timeoutMilliseconds, CancellationToken cancellationToken)
        {
            BrowserCommandCalls.Add($"{nameof(WaitForFunctionAsync)}:{resourceName}:{function}:{timeoutMilliseconds}");
            return Task.FromResult("""{"action":"wait-for-function"}""");
        }

        public Task<string> CloseActiveSessionAsync(string resourceName, CancellationToken cancellationToken)
        {
            BrowserCommandCalls.Add($"{nameof(CloseActiveSessionAsync)}:{resourceName}");
            return Task.FromResult("""{"action":"close"}""");
        }
    }

    private sealed record SessionStartCall(BrowserLogsResource Resource, BrowserConfiguration Configuration, string ResourceName, Uri Url);

    private sealed class FakeBrowserLogsRunningSessionFactory : IBrowserLogsRunningSessionFactory
    {
        public List<FakeBrowserLogsRunningSession> Sessions { get; } = [];
        public List<BrowserConfiguration> Configurations { get; } = [];
        public Exception? NextStartException { get; set; }
        public BrowserHostOwnership NextBrowserHostOwnership { get; set; } = BrowserHostOwnership.Owned;
        public int? NextProcessId { get; set; }
        public bool NextProcessIdIsNull { get; set; }

        public Task<IBrowserLogsRunningSession> StartSessionAsync(
            BrowserConfiguration configuration,
            string resourceName,
            Uri url,
            string sessionId,
            ILogger resourceLogger,
            CancellationToken cancellationToken)
        {
            Configurations.Add(configuration);

            if (NextStartException is { } exception)
            {
                NextStartException = null;
                return Task.FromException<IBrowserLogsRunningSession>(exception);
            }

            var sessionNumber = Sessions.Count + 1;
            var processId = NextProcessIdIsNull ? (int?)null : NextProcessId ?? 1000 + sessionNumber;
            var session = new FakeBrowserLogsRunningSession(
                sessionId,
                $"/fake/browser-{sessionNumber}",
                processId,
                sessionNumber,
                NextBrowserHostOwnership,
                startedAt: DateTime.UtcNow);

            Sessions.Add(session);
            NextBrowserHostOwnership = BrowserHostOwnership.Owned;
            NextProcessId = null;
            NextProcessIdIsNull = false;

            return Task.FromResult<IBrowserLogsRunningSession>(session);
        }
    }

    private sealed class FakeBrowserLogsRunningSession(
        string sessionId,
        string browserExecutable,
        int? processId,
        int sessionNumber,
        BrowserHostOwnership browserHostOwnership,
        DateTime startedAt) : IBrowserLogsRunningSession
    {
        private TaskCompletionSource<object?> _completionObserverGate = CreateSignaledTaskCompletionSource();
        private readonly TaskCompletionSource<(int ExitCode, Exception? Error)> _completionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task? _completionObserverTask;

        public string SessionId { get; } = sessionId;

        public string BrowserExecutable { get; } = browserExecutable;

        public Uri BrowserDebugEndpoint { get; } = new($"ws://127.0.0.1:{9000 + sessionNumber}/devtools/browser/browser-{sessionNumber}");

        public BrowserHostOwnership BrowserHostOwnership { get; } = browserHostOwnership;

        public int? ProcessId { get; } = processId;

        public DateTime StartedAt { get; } = startedAt;

        public string TargetId { get; } = $"target-{sessionNumber}";

        public int StopCallCount { get; private set; }

        public byte[] ScreenshotBytes { get; set; } = [0x89, 0x50, 0x4e, 0x47];

        public Task CompletionObserverStarted => CompletionObserverStartedSource.Task;

        private TaskCompletionSource<object?> CompletionObserverStartedSource { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task StartCompletionObserver(Func<int?, Exception?, Task> onCompleted)
        {
            _completionObserverTask = ObserveCompletionAsync(onCompleted);
            return _completionObserverTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            StopCallCount++;
            _completionSource.TrySetResult((0, null));
            return Task.CompletedTask;
        }

        public Task<byte[]> CaptureScreenshotAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(ScreenshotBytes);
        }

        public Task NavigateAsync(Uri url, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task<string> EvaluateJsonAsync(string expression, TimeSpan? timeout, CancellationToken cancellationToken)
        {
            return Task.FromResult("""{"action":"evaluate"}""");
        }

        public async Task CompleteAsync(int exitCode, Exception? error = null)
        {
            _completionSource.TrySetResult((exitCode, error));
            await (_completionObserverTask ?? Task.CompletedTask);
        }

        public void PauseCompletionObserver()
        {
            CompletionObserverStartedSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _completionObserverGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void ResumeCompletionObserver()
        {
            _completionObserverGate.TrySetResult(null);
        }

        private async Task ObserveCompletionAsync(Func<int?, Exception?, Task> onCompleted)
        {
            var (exitCode, error) = await _completionSource.Task;
            CompletionObserverStartedSource.TrySetResult(null);
            await _completionObserverGate.Task;
            await onCompleted(exitCode, error);
        }

        private static TaskCompletionSource<object?> CreateSignaledTaskCompletionSource()
        {
            var source = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            source.TrySetResult(null);
            return source;
        }
    }

    private sealed class RecordingUserSecretsManager : IUserSecretsManager
    {
        public Dictionary<string, string> Secrets { get; } = new(StringComparer.Ordinal);

        public HashSet<string> DeletedSecrets { get; } = new(StringComparer.Ordinal);

        public HashSet<string> FailingSetSecretNames { get; } = new(StringComparer.Ordinal);

        public HashSet<string> FailingDeleteSecretNames { get; } = new(StringComparer.Ordinal);

        public bool IsAvailable { get; init; } = true;

        public string FilePath => Path.Combine(AppContext.BaseDirectory, "test-secrets.json");

        public bool TrySetSecret(string name, string value)
        {
            if (FailingSetSecretNames.Contains(name))
            {
                return false;
            }

            Secrets[name] = value;
            DeletedSecrets.Remove(name);
            return true;
        }

        public bool TryDeleteSecret(string name)
        {
            if (FailingDeleteSecretNames.Contains(name))
            {
                return false;
            }

            Secrets.Remove(name);
            DeletedSecrets.Add(name);
            return true;
        }

        public void GetOrSetSecret(IConfigurationManager configuration, string name, Func<string> valueGenerator)
        {
            configuration[name] ??= valueGenerator();
        }

        public Task SaveStateAsync(JsonObject state, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static JsonSerializerOptions BrowserSessionPropertyJsonOptions { get; } = new(JsonSerializerDefaults.Web);

    private sealed record BrowserSessionPropertyValue(
        string SessionId,
        string Browser,
        string BrowserExecutable,
        int? ProcessId,
        string? Profile,
        DateTime StartedAt,
        string TargetUrl,
        string BrowserHostOwnership,
        string CdpEndpoint,
        string PageCdpEndpoint,
        string TargetId);
}

#pragma warning restore ASPIREUSERSECRETS001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning restore ASPIREINTERACTION001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning restore ASPIREBROWSERLOGS001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
