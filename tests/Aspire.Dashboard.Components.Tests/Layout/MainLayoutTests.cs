// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Layout;
using Aspire.Dashboard.Components.Resize;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Tests.Shared;
using Aspire.Dashboard.Utils;
using Bunit;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components.Components.Tooltip;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Layout;

[UseCulture("en-US")]
public partial class MainLayoutTests : DashboardTestContext
{
    [Fact]
    public async Task OnInitialize_UnsecuredOtlp_NotDismissed_DisplayMessageBar()
    {
        // Arrange
        var testLocalStorage = new TestLocalStorage();
        var messageService = new MessageService();

        SetupMainLayoutServices(localStorage: testLocalStorage, messageService: messageService);

        Message? message = null;
        var messageShownTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        messageService.OnMessageItemsUpdatedAsync += () =>
        {
            message = messageService.AllMessages.Single();
            messageShownTcs.TrySetResult();
            return Task.CompletedTask;
        };

        testLocalStorage.OnGetUnprotectedAsync = key =>
        {
            switch (key)
            {
                case BrowserStorageKeys.UnsecuredTelemetryMessageDismissedKey:
                case BrowserStorageKeys.UnsecuredEndpointMessageDismissedKey:
                    return (false, false);
                default:
                    throw new InvalidOperationException("Unexpected key.");
            }
        };

        var dismissedSettingSetTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        testLocalStorage.OnSetUnprotectedAsync = (key, value) =>
        {
            switch (key)
            {
                case BrowserStorageKeys.UnsecuredTelemetryMessageDismissedKey:
                case BrowserStorageKeys.UnsecuredEndpointMessageDismissedKey:
                    dismissedSettingSetTcs.TrySetResult((bool)value!);
                    break;
                default:
                    throw new InvalidOperationException("Unexpected key.");
            }
        };

        // Act
        var cut = RenderComponent<MainLayout>(builder =>
        {
            builder.Add(p => p.ViewportInformation, new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false));
        });

        // Assert
        await messageShownTcs.Task.DefaultTimeout();

        Assert.NotNull(message);

        message.Close();

        Assert.True(await dismissedSettingSetTcs.Task.DefaultTimeout());
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task OnInitialize_UnsecuredOtlp_Dismissed_NoMessageBar(bool unsecuredTelemetryMessageDismissedKey, bool unsecuredEndpointMessageDismissedKey)
    {
        // Arrange
        var testLocalStorage = new TestLocalStorage();
        var messageService = new MessageService();

        SetupMainLayoutServices(localStorage: testLocalStorage, messageService: messageService);

        var messageShownTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        messageService.OnMessageItemsUpdatedAsync += () =>
        {
            messageShownTcs.TrySetResult();
            return Task.CompletedTask;
        };

        testLocalStorage.OnGetUnprotectedAsync = key =>
        {
            switch (key)
            {
                case BrowserStorageKeys.UnsecuredTelemetryMessageDismissedKey:
                    return (unsecuredTelemetryMessageDismissedKey, unsecuredTelemetryMessageDismissedKey);
                case BrowserStorageKeys.UnsecuredEndpointMessageDismissedKey:
                    return (unsecuredEndpointMessageDismissedKey, unsecuredEndpointMessageDismissedKey);
                default:
                    throw new InvalidOperationException("Unexpected key.");
            }
        };

        // Act
        var cut = RenderComponent<MainLayout>(builder =>
        {
            builder.Add(p => p.ViewportInformation, new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false));
        });

        // Assert
        var timeoutTask = Task.Delay(100);
        var completedTask = await Task.WhenAny(messageShownTcs.Task, timeoutTask).DefaultTimeout();

        // It's hard to test something not happening.
        // In this case of checking for a message, apply a small display and then double check that no message was displayed.
        Assert.True(completedTask != messageShownTcs.Task, "No message bar should be displayed.");
        Assert.Empty(messageService.AllMessages);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    public async Task OnInitialize_UnsecuredOtlp_SuppressConfigured_NoMessageBar(bool expectMessageBar, bool telemetrySuppressUnsecuredMessage, bool mcpSuppressUnsecuredMessage)
    {
        // Arrange
        var testLocalStorage = new TestLocalStorage();
        var messageService = new MessageService();

        SetupMainLayoutServices(localStorage: testLocalStorage, messageService: messageService, configureOptions: o =>
        {
            o.Otlp.SuppressUnsecuredMessage = telemetrySuppressUnsecuredMessage;
            o.Mcp.SuppressUnsecuredMessage = mcpSuppressUnsecuredMessage;
        });

        var messageShownTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        messageService.OnMessageItemsUpdatedAsync += () =>
        {
            messageShownTcs.TrySetResult();
            return Task.CompletedTask;
        };

        testLocalStorage.OnGetUnprotectedAsync = key =>
        {
            switch (key)
            {
                case BrowserStorageKeys.UnsecuredTelemetryMessageDismissedKey:
                case BrowserStorageKeys.UnsecuredEndpointMessageDismissedKey:
                    return (false, false); // Message not dismissed, but should be suppressed by config if suppressUnsecuredMessage is true
                default:
                    throw new InvalidOperationException("Unexpected key.");
            }
        };

        // Act
        var cut = RenderComponent<MainLayout>(builder =>
        {
            builder.Add(p => p.ViewportInformation, new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false));
        });

        // Assert
        if (!expectMessageBar)
        {
            var timeoutTask = Task.Delay(100);
            var completedTask = await Task.WhenAny(messageShownTcs.Task, timeoutTask).DefaultTimeout();

            // When suppressed, no message should be displayed
            Assert.True(completedTask != messageShownTcs.Task, "No message bar should be displayed when suppressed by configuration.");
            Assert.Empty(messageService.AllMessages);
        }
        else
        {
            // When not suppressed, message should be displayed since it wasn't dismissed
            await messageShownTcs.Task.DefaultTimeout();
            Assert.NotEmpty(messageService.AllMessages);
        }
    }

    private void SetupMainLayoutServices(TestLocalStorage? localStorage = null, MessageService? messageService = null, Action<DashboardOptions>? configureOptions = null)
    {
        FluentUISetupHelpers.AddCommonDashboardServices(this, localStorage: localStorage, messageService: messageService);
        
        Services.AddOptions();
        Services.AddSingleton<IThemeResolver, TestThemeResolver>();
        Services.AddSingleton<IDashboardClient, TestDashboardClient>();
        Services.AddSingleton<ITooltipService, TooltipService>();
        Services.AddSingleton<IToastService, ToastService>();
        Services.Configure<DashboardOptions>(o =>
        {
            o.Otlp.AuthMode = OtlpAuthMode.Unsecured;
            o.Mcp.AuthMode = McpAuthMode.Unsecured;
            configureOptions?.Invoke(o);
        });

        FluentUISetupHelpers.SetupFluentDialogProvider(this);
        FluentUISetupHelpers.SetupFluentOverflow(this);
        FluentUISetupHelpers.SetupFluentAnchor(this);

        var themeModule = JSInterop.SetupModule("/js/app-theme.js");

        JSInterop.SetupModule("window.registerGlobalKeydownListener", _ => true);
        JSInterop.SetupModule("window.registerOpenTextVisualizerOnClick", _ => true);

        JSInterop.Setup<BrowserInfo>("window.getBrowserInfo").SetResult(new BrowserInfo { TimeZone = "abc", UserAgent = "mozilla" });
    }
}
