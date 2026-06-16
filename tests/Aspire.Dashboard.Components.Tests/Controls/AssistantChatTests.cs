// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using AngleSharp.Dom;
using Aspire.Dashboard.Components.Pages;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Model.Assistant;
using Aspire.Dashboard.Model.Assistant.Prompts;
using Aspire.Dashboard.Model.BrowserStorage;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.Resources;
using Aspire.Dashboard.Telemetry;
using Aspire.Dashboard.Tests;
using Aspire.Dashboard.Tests.Shared;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Controls;

public class AssistantChatTests : DashboardTestContext
{
    [Fact]
    public async Task FeedbackButtonsExposePressedState()
    {
        var chatMessage = new ChatViewModel(isUserMessage: false)
        {
            IsComplete = true,
            PromptText = "Answer"
        };
        chatMessage.SetText("Answer");

        var chatState = new AssistantChatState();
        chatState.VisibleChatMessages.Add(chatMessage);

        var chatViewModel = CreateChatViewModel(chatState);

        var cut = RenderComponent<AssistantChat>(builder =>
        {
            builder.Add(p => p.ChatViewModel, chatViewModel);
            builder.Add(p => p.Class, "chat");
            builder.Add(p => p.ModelInitialized, EventCallback.Empty);
        });

        Assert.Equal("false", FindButton(cut, nameof(AIAssistant.LikeMessageButtonText)).GetAttribute("aria-pressed"));
        Assert.Equal("false", FindButton(cut, nameof(AIAssistant.DislikeMessageButtonText)).GetAttribute("aria-pressed"));

        await FindButton(cut, nameof(AIAssistant.LikeMessageButtonText)).ClickAsync(new MouseEventArgs());

        Assert.Equal("true", FindButton(cut, nameof(AIAssistant.LikeMessageButtonText)).GetAttribute("aria-pressed"));
        Assert.Equal("false", FindButton(cut, nameof(AIAssistant.DislikeMessageButtonText)).GetAttribute("aria-pressed"));

        await FindButton(cut, nameof(AIAssistant.LikeMessageButtonText)).ClickAsync(new MouseEventArgs());

        Assert.Equal("false", FindButton(cut, nameof(AIAssistant.LikeMessageButtonText)).GetAttribute("aria-pressed"));
        Assert.Equal("false", FindButton(cut, nameof(AIAssistant.DislikeMessageButtonText)).GetAttribute("aria-pressed"));

        await FindButton(cut, nameof(AIAssistant.DislikeMessageButtonText)).ClickAsync(new MouseEventArgs());

        Assert.Equal("false", FindButton(cut, nameof(AIAssistant.LikeMessageButtonText)).GetAttribute("aria-pressed"));
        Assert.Equal("true", FindButton(cut, nameof(AIAssistant.DislikeMessageButtonText)).GetAttribute("aria-pressed"));
    }

    private AssistantChatViewModel CreateChatViewModel(AssistantChatState chatState)
    {
        var aiContextProvider = new TestAIContextProvider { Enabled = true };
        var dashboardOptions = new DashboardOptions();
        var optionsMonitor = new TestOptionsMonitor<DashboardOptions>(dashboardOptions);
        var assistantLocalizer = new TestStringLocalizer<AIAssistant>();
        var controlsLocalizer = new TestStringLocalizer<ControlsStrings>();

        FluentUISetupHelpers.AddCommonDashboardServices(this);

        Services.AddLogging();
        Services.AddSingleton<IConfiguration>(new ConfigurationManager());
        Services.AddSingleton<IDashboardClient>(new TestDashboardClient());
        Services.AddSingleton<IAIContextProvider>(aiContextProvider);
        Services.AddSingleton<IStringLocalizer<AIAssistant>>(assistantLocalizer);
        Services.AddSingleton<IStringLocalizer<ControlsStrings>>(controlsLocalizer);

        FluentUISetupHelpers.SetupFluentUIComponents(this);
        FluentUISetupHelpers.SetupFluentButton(this);

        var assistantChatModule = JSInterop.SetupModule("./Components/Controls/AssistantChat.razor.js");
        assistantChatModule.SetupVoid("initializeAssistantChat", _ => true);

        var dataContext = new AssistantChatDataContext(
            Services.GetRequiredService<TelemetryRepository>(),
            Services.GetRequiredService<IDashboardClient>(),
            [],
            assistantLocalizer,
            optionsMonitor);

        var telemetryService = new DashboardTelemetryService(
            NullLogger<DashboardTelemetryService>.Instance,
            Services.GetRequiredService<IDashboardTelemetrySender>());

        var chatViewModel = new AssistantChatViewModel(
            Services.GetRequiredService<TelemetryRepository>(),
            Services.GetRequiredService<IConfiguration>(),
            Services.GetRequiredService<ILocalStorage>(),
            NullLoggerFactory.Instance,
            aiContextProvider,
            new ChatClientFactory(Services.GetRequiredService<IConfiguration>(), NullLoggerFactory.Instance, optionsMonitor),
            dataContext,
            Services,
            assistantLocalizer,
            controlsLocalizer,
            telemetryService,
            new ComponentTelemetryContextProvider(telemetryService),
            new IceBreakersBuilder(new TestStringLocalizer<AIPrompts>()),
            optionsMonitor)
        {
            DisplayState = AssistantChatDisplayState.Chat
        };

        // AssistantChatViewModel doesn't expose a way to seed completed assistant responses without
        // initializing GHCP clients. Keep this test focused on the rendered component state.
        typeof(AssistantChatViewModel)
            .GetField("_chatState", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(chatViewModel, chatState);

        return chatViewModel;
    }

    private static IElement FindButton(IRenderedComponent<AssistantChat> cut, string resourceName)
    {
        return cut.Find($"fluent-button[aria-label='Localized:{resourceName}']");
    }

    private sealed class TestOptionsMonitor<T>(T currentValue) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = currentValue;

        public T Get(string? name)
        {
            return CurrentValue;
        }

        public IDisposable? OnChange(Action<T, string?> listener)
        {
            return null;
        }
    }
}
