// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Dialogs;
using Aspire.Dashboard.Components.Layout;
using Aspire.Dashboard.Components.Resize;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.Assistant;
using Aspire.Dashboard.Tests;
using Aspire.Dashboard.Tests.Shared;
using Aspire.Dashboard.Utils;
using Aspire.Shared;
using Aspire.Tests.Shared;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components.Components.Tooltip;
using Microsoft.JSInterop;
using Xunit;
using AssistantModalDialog = Aspire.Dashboard.Components.Dialogs.AssistantModalDialog;
using AssistantSidebarDialog = Aspire.Dashboard.Components.Dialogs.AssistantSidebarDialog;

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
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task OnInitialize_UnsecuredOtlp_SuppressConfigured_NoMessageBar(bool expectMessageBar, bool telemetrySuppressUnsecuredMessage)
    {
        // Arrange
        var testLocalStorage = new TestLocalStorage();
        var messageService = new MessageService();

        SetupMainLayoutServices(localStorage: testLocalStorage, messageService: messageService, configureOptions: o =>
        {
            o.Otlp.SuppressUnsecuredMessage = telemetrySuppressUnsecuredMessage;
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

    [Theory]
    [InlineData(true, "dashboard-help-button", "HelpDialog", "dashboard-help-button")]
    [InlineData(true, "dashboard-settings-button", "SettingsDialog", "dashboard-settings-button")]
    [InlineData(false, "dashboard-navigation-button", "HelpDialog", "dashboard-navigation-button")]
    [InlineData(false, "dashboard-navigation-button", "SettingsDialog", "dashboard-navigation-button")]
    public async Task HeaderDialogClose_RestoresFocusToLaunchButton(bool isDesktop, string launchButtonId, string expectedDialogId, string expectedFocusId)
    {
        DialogParameters? capturedParameters = null;
        TestDialogService? dialogService = null;
        dialogService = new TestDialogService(onShowDialog: (_, parameters) =>
        {
            capturedParameters = parameters;
            return Task.FromResult<IDialogReference>(new DialogReference(parameters.Id, dialogService!));
        });

        SetupMainLayoutServices(dialogService: dialogService);
        JSInterop.SetupVoid("focusElement", _ => true);

        var cut = RenderComponent<MainLayout>(builder =>
        {
            builder.Add(p => p.ViewportInformation, new ViewportInformation(IsDesktop: isDesktop, IsUltraLowHeight: false, IsUltraLowWidth: false));
        });

        if (isDesktop)
        {
            await cut.InvokeAsync(() => cut.Find($"#{launchButtonId}").Click());
        }
        else
        {
            var menuItemName = expectedDialogId == "HelpDialog"
                ? "Help"
                : "Settings";

            await cut.InvokeAsync(() => cut.Find("#dashboard-navigation-button").Click());
            await cut.InvokeAsync(() => cut.FindAll("fluent-menu-item").Single(item => item.TextContent.Contains(menuItemName, StringComparison.OrdinalIgnoreCase)).Click());
        }

        Assert.NotNull(capturedParameters);
        Assert.Equal(expectedDialogId, capturedParameters.Id);

        await cut.InvokeAsync(() => capturedParameters.OnDialogClosing.InvokeAsync(null!));

        cut.WaitForAssertion(() =>
        {
            Assert.Contains(JSInterop.Invocations, invocation =>
                invocation.Identifier == "focusElement" &&
                invocation.Arguments.Count == 1 &&
                string.Equals((string?)invocation.Arguments[0], expectedFocusId, StringComparison.Ordinal));
        });
    }

    [Theory]
    [InlineData("Report a bug", nameof(FeedbackIssueKind.Bug))]
    [InlineData("Suggest an idea", nameof(FeedbackIssueKind.Idea))]
    [InlineData("General feedback", nameof(FeedbackIssueKind.General))]
    public async Task FeedbackMenu_Items_ShowFeedbackDialog(string menuText, string expectedKind)
    {
        DialogParameters? capturedParameters = null;
        object? capturedData = null;
        TestDialogService? dialogService = null;
        dialogService = new TestDialogService(onShowDialog: (data, parameters) =>
        {
            capturedData = data;
            capturedParameters = parameters;
            return Task.FromResult<IDialogReference>(new DialogReference(parameters.Id, dialogService!));
        });

        SetupMainLayoutServices(dialogService: dialogService);

        var cut = RenderComponent<MainLayout>(builder =>
        {
            builder.Add(p => p.ViewportInformation, new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false));
        });

        await cut.InvokeAsync(() => cut.Find("#dashboard-feedback-button").Click());
        await cut.InvokeAsync(() => cut.FindAll("fluent-menu-item").Single(item => item.TextContent.Contains(menuText, StringComparison.OrdinalIgnoreCase)).Click());

        Assert.NotNull(capturedParameters);
        Assert.Equal("FeedbackDialog", capturedParameters.Id);
        Assert.Equal(menuText, capturedParameters.Title);
        var viewModel = Assert.IsType<FeedbackDialogViewModel>(capturedData);
        Assert.Equal(expectedKind, viewModel.Kind);
        Assert.Equal(menuText, viewModel.Title);
    }

    [Fact]
    public async Task FeedbackDialog_ReportBug_OpensIssueTemplateUrlWithEditableDetails()
    {
        SetupMainLayoutServices(feedbackDiagnosticProvider: new TestDashboardFeedbackDiagnosticProvider(
            doctorOutput: """{"sdk":"10.0.301"}""",
            additionalContext: "- Posted from: Dashboard"));
        FluentUISetupHelpers.SetupFluentUIComponents(this);
        FluentUISetupHelpers.SetupFluentInputLabel(this);
        FluentUISetupHelpers.SetupFluentTextField(this);
        JSInterop.SetupVoid("open", _ => true);

        var cut = FluentUISetupHelpers.RenderDialogProvider(this);
        var dialogService = Services.GetRequiredService<IDialogService>();
        await dialogService.ShowDialogAsync<FeedbackDialog>(
            new FeedbackDialogViewModel(nameof(FeedbackIssueKind.Bug), "Report a bug"),
            new DialogParameters
            {
                Title = "Report a bug",
                PrimaryAction = null,
                SecondaryAction = null
            });

        cut.WaitForAssertion(() =>
        {
            Assert.True(cut.HasComponent<FeedbackDialog>());
            Assert.Contains("sdk", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("Issues created are public on the", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("GitHub repo.", cut.Markup, StringComparison.Ordinal);
        });
        Assert.True(cut.Find($"#{FeedbackDialog.MainTextInputId}").HasAttribute("required"));
        var publicIssueMessage = cut.Find(".feedback-dialog-public-message");
        Assert.Equal("note", publicIssueMessage.GetAttribute("role"));
        var publicIssueLink = publicIssueMessage.QuerySelector("fluent-anchor");
        Assert.NotNull(publicIssueLink);
        Assert.Equal("https://github.com/microsoft/aspire", publicIssueLink.GetAttribute("href"));
        Assert.Equal("_blank", publicIssueLink.GetAttribute("target"));
        Assert.Contains("microsoft/aspire", publicIssueLink.TextContent, StringComparison.Ordinal);
        var footerButtons = cut.FindAll("fluent-button")
            .Where(button => button.TextContent.Contains("Open issue", StringComparison.OrdinalIgnoreCase) || button.TextContent.Contains("Cancel", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.Collection(
            footerButtons,
            button => Assert.Contains("Open issue", button.TextContent, StringComparison.OrdinalIgnoreCase),
            button => Assert.Contains("Cancel", button.TextContent, StringComparison.OrdinalIgnoreCase));

        cut.Find($"#{FeedbackDialog.TitleInputId}").Change("Bug title");
        cut.Find($"#{FeedbackDialog.MainTextInputId}").Input("Bug details");
        cut.Find($"#{FeedbackDialog.DoctorOutputInputId}").Input("""{"sdk":"edited"}""");
        cut.Find($"#{FeedbackDialog.AdditionalContextInputId}").Input("Edited context");
        await cut.InvokeAsync(() => cut.FindAll("fluent-button").Single(button => button.TextContent.Contains("Open issue", StringComparison.OrdinalIgnoreCase)).Click());

        Assert.Contains(JSInterop.Invocations, invocation =>
            invocation.Identifier == "open" &&
            TryGetOpenArguments(invocation.Arguments, out var url, out var target) &&
            url.StartsWith("https://github.com/microsoft/aspire/issues/new?", StringComparison.Ordinal) &&
            url.Contains("template=10_bug_report.yml", StringComparison.Ordinal) &&
            url.Contains("title=Bug%20title", StringComparison.Ordinal) &&
            url.Contains("description=Bug%20details", StringComparison.Ordinal) &&
            url.Contains("aspire-doctor-output=", StringComparison.Ordinal) &&
            url.Contains("sdk", StringComparison.Ordinal) &&
            url.Contains("edited", StringComparison.Ordinal) &&
            url.Contains("additional-context=Edited%20context", StringComparison.Ordinal) &&
            string.Equals(target, "_blank", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FeedbackDialog_ReportBug_ShowsProgressRingBesideDoctorOutputWhileLoading()
    {
        var diagnosticProvider = new WaitingDashboardFeedbackDiagnosticProvider();
        SetupMainLayoutServices(feedbackDiagnosticProvider: diagnosticProvider);
        FluentUISetupHelpers.SetupFluentUIComponents(this);
        FluentUISetupHelpers.SetupFluentInputLabel(this);
        FluentUISetupHelpers.SetupFluentTextField(this);

        var cut = FluentUISetupHelpers.RenderDialogProvider(this);
        var dialogService = Services.GetRequiredService<IDialogService>();
        await dialogService.ShowDialogAsync<FeedbackDialog>(
            new FeedbackDialogViewModel(nameof(FeedbackIssueKind.Bug), "Report a bug"),
            new DialogParameters
            {
                Title = "Report a bug",
                PrimaryAction = null,
                SecondaryAction = null
            });

        try
        {
            cut.WaitForAssertion(() =>
            {
                var doctorInputLine = cut.Find($"#{FeedbackDialog.DoctorOutputInputId}").ParentElement;
                Assert.NotNull(doctorInputLine);
                Assert.Contains("feedback-input-line", doctorInputLine.ClassList);
                Assert.NotEmpty(doctorInputLine.QuerySelectorAll("fluent-progress-ring.feedback-input-progress"));
                var doctorOutput = cut.Find($"#{FeedbackDialog.DoctorOutputInputId}");
                var doctorInputLineChildren = doctorInputLine.Children.ToList();
                Assert.Equal(doctorOutput.Id, doctorInputLineChildren[0].Id);
                Assert.Equal("fluent-progress-ring", doctorInputLineChildren[1].LocalName);
                Assert.False(doctorOutput.HasAttribute("disabled"));
                Assert.True(doctorOutput.HasAttribute("readonly"));
                Assert.Equal("true", doctorOutput.GetAttribute("aria-busy"));
                Assert.Contains("Collecting Aspire doctor output...", doctorInputLine.InnerHtml, StringComparison.Ordinal);
                Assert.Equal(string.Empty, doctorInputLine.TextContent.Trim());
            });
        }
        finally
        {
            diagnosticProvider.SetResult();
        }
    }

    [Fact]
    public async Task FeedbackDialog_ReportBug_PreservesAdditionalContextEditedWhileLoading()
    {
        var diagnosticProvider = new WaitingDashboardFeedbackDiagnosticProvider();
        SetupMainLayoutServices(feedbackDiagnosticProvider: diagnosticProvider);
        FluentUISetupHelpers.SetupFluentUIComponents(this);
        FluentUISetupHelpers.SetupFluentInputLabel(this);
        FluentUISetupHelpers.SetupFluentTextField(this);
        JSInterop.SetupVoid("open", _ => true);

        var cut = FluentUISetupHelpers.RenderDialogProvider(this);
        var dialogService = Services.GetRequiredService<IDialogService>();
        await dialogService.ShowDialogAsync<FeedbackDialog>(
            new FeedbackDialogViewModel(nameof(FeedbackIssueKind.Bug), "Report a bug"),
            new DialogParameters
            {
                Title = "Report a bug",
                PrimaryAction = null,
                SecondaryAction = null
            });

        try
        {
            cut.WaitForAssertion(() => Assert.True(cut.Find($"#{FeedbackDialog.DoctorOutputInputId}").HasAttribute("readonly")));

            cut.Find($"#{FeedbackDialog.TitleInputId}").Change("Bug title");
            cut.Find($"#{FeedbackDialog.MainTextInputId}").Input("Bug details");
            cut.Find($"#{FeedbackDialog.AdditionalContextInputId}").Input("Edited while loading");
            diagnosticProvider.SetResult();

            cut.WaitForAssertion(() =>
            {
                Assert.False(cut.Find($"#{FeedbackDialog.DoctorOutputInputId}").HasAttribute("readonly"));
            });
            await cut.InvokeAsync(() => cut.FindAll("fluent-button").Single(button => button.TextContent.Contains("Open issue", StringComparison.OrdinalIgnoreCase)).Click());

            Assert.Contains(JSInterop.Invocations, invocation =>
                invocation.Identifier == "open" &&
                TryGetOpenArguments(invocation.Arguments, out var url, out _) &&
                url.Contains("additional-context=Edited%20while%20loading", StringComparison.Ordinal));
        }
        finally
        {
            diagnosticProvider.SetResult();
        }
    }

    [Theory]
    [InlineData(nameof(FeedbackIssueKind.Bug), "Report a bug", 1)]
    [InlineData(nameof(FeedbackIssueKind.Idea), "Suggest an idea", 0)]
    [InlineData(nameof(FeedbackIssueKind.General), "General feedback", 0)]
    public async Task FeedbackDialog_CapturesAppHostAndDoctorContext_OnlyForBugReports(string kind, string title, int expectedCaptures)
    {
        var diagnosticProvider = new TestDashboardFeedbackDiagnosticProvider(
            doctorOutput: """{"sdk":"10.0.301"}""",
            additionalContext: "- Posted from: Dashboard");
        SetupMainLayoutServices(feedbackDiagnosticProvider: diagnosticProvider);
        FluentUISetupHelpers.SetupFluentUIComponents(this);
        FluentUISetupHelpers.SetupFluentInputLabel(this);
        FluentUISetupHelpers.SetupFluentTextField(this);
        JSInterop.SetupVoid("open", _ => true);

        var cut = FluentUISetupHelpers.RenderDialogProvider(this);
        var dialogService = Services.GetRequiredService<IDialogService>();
        await dialogService.ShowDialogAsync<FeedbackDialog>(
            new FeedbackDialogViewModel(kind, title),
            new DialogParameters
            {
                Title = title,
                PrimaryAction = null,
                SecondaryAction = null
            });

        // The AppHost description and the `aspire doctor` capture are only relevant to bug reports:
        // the dialog requests the AppHost line (includeAppHostInfo) and launches the doctor capture
        // (which spawns the CLI) only for bugs. Idea/general feedback must leave both counts at zero.
        cut.WaitForAssertion(() =>
        {
            Assert.True(cut.HasComponent<FeedbackDialog>());
            Assert.Equal(expectedCaptures, diagnosticProvider.AppHostInfoRequestedCount);
            Assert.Equal(expectedCaptures, diagnosticProvider.DoctorOutputCaptureCount);
        });
    }

    [Theory]
    [InlineData(true, false, "dashboard-help-button", "HelpDialog", "dashboard-navigation-button")]
    [InlineData(true, false, "dashboard-settings-button", "SettingsDialog", "dashboard-navigation-button")]
    [InlineData(false, true, "dashboard-navigation-button", "HelpDialog", "dashboard-help-button")]
    [InlineData(false, true, "dashboard-navigation-button", "SettingsDialog", "dashboard-settings-button")]
    public async Task HeaderDialogClose_AfterViewportChange_RestoresFocusToVisibleLaunchButton(
        bool initialIsDesktop,
        bool closingIsDesktop,
        string launchButtonId,
        string expectedDialogId,
        string expectedFocusId)
    {
        DialogParameters? capturedParameters = null;
        TestDialogService? dialogService = null;
        dialogService = new TestDialogService(onShowDialog: (_, parameters) =>
        {
            capturedParameters = parameters;
            return Task.FromResult<IDialogReference>(new DialogReference(parameters.Id, dialogService!));
        });

        SetupMainLayoutServices(dialogService: dialogService);
        JSInterop.SetupVoid("focusElement", _ => true);

        var cut = RenderComponent<CascadingValue<ViewportInformation>>(builder =>
        {
            builder.Add(p => p.Value, new ViewportInformation(IsDesktop: initialIsDesktop, IsUltraLowHeight: false, IsUltraLowWidth: false));
            builder.AddChildContent<MainLayout>();
        });

        if (initialIsDesktop)
        {
            await cut.InvokeAsync(() => cut.Find($"#{launchButtonId}").Click());
        }
        else
        {
            var menuItemName = expectedDialogId == "HelpDialog"
                ? "Help"
                : "Settings";

            await cut.InvokeAsync(() => cut.Find("#dashboard-navigation-button").Click());
            await cut.InvokeAsync(() => cut.FindAll("fluent-menu-item").Single(item => item.TextContent.Contains(menuItemName, StringComparison.OrdinalIgnoreCase)).Click());
        }

        Assert.NotNull(capturedParameters);
        Assert.Equal(expectedDialogId, capturedParameters.Id);

        cut.SetParametersAndRender(parameters =>
        {
            parameters.Add(p => p.Value, new ViewportInformation(IsDesktop: closingIsDesktop, IsUltraLowHeight: false, IsUltraLowWidth: false));
            parameters.AddChildContent<MainLayout>();
        });

        await cut.InvokeAsync(() => capturedParameters.OnDialogClosing.InvokeAsync(null!));

        cut.WaitForAssertion(() =>
        {
            Assert.Contains(JSInterop.Invocations, invocation =>
                invocation.Identifier == "focusElement" &&
                invocation.Arguments.Count == 1 &&
                string.Equals((string?)invocation.Arguments[0], expectedFocusId, StringComparison.Ordinal));
        });
    }

    [Theory]
    [InlineData(AspireKeyboardShortcut.Help, "dashboard-help-button", "HelpDialog")]
    [InlineData(AspireKeyboardShortcut.Settings, "dashboard-settings-button", "SettingsDialog")]
    public async Task HeaderDialogShortcutClose_RestoresFocusToLaunchButton(AspireKeyboardShortcut shortcut, string launchButtonId, string expectedDialogId)
    {
        DialogParameters? capturedParameters = null;
        TestDialogService? dialogService = null;
        dialogService = new TestDialogService(onShowDialog: (_, parameters) =>
        {
            capturedParameters = parameters;
            return Task.FromResult<IDialogReference>(new DialogReference(parameters.Id, dialogService!));
        });

        SetupMainLayoutServices(dialogService: dialogService);
        JSInterop.SetupVoid("focusElement", _ => true);

        var cut = RenderComponent<MainLayout>(builder =>
        {
            builder.Add(p => p.ViewportInformation, new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false));
        });

        await cut.InvokeAsync(() => cut.Instance.OnPageKeyDownAsync(shortcut));

        Assert.NotNull(capturedParameters);
        Assert.Equal(expectedDialogId, capturedParameters.Id);

        await cut.InvokeAsync(() => capturedParameters.OnDialogClosing.InvokeAsync(null!));

        cut.WaitForAssertion(() =>
        {
            Assert.Contains(JSInterop.Invocations, invocation =>
                invocation.Identifier == "focusElement" &&
                invocation.Arguments.Count == 1 &&
                string.Equals((string?)invocation.Arguments[0], launchButtonId, StringComparison.Ordinal));
        });
    }

    [Fact]
    public async Task AssistantSidebarHide_RestoresFocusToLaunchButton()
    {
        var aiContextProvider = new TestAIContextProvider();
        SetupMainLayoutServices(aiContextProvider: aiContextProvider);
        JSInterop.SetupVoid("focusElement", _ => true);

        var cut = RenderComponent<MainLayout>(builder =>
        {
            builder.Add(p => p.ViewportInformation, new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false));
        });

        typeof(MainLayout)
            .GetField("_assistantReturnFocusElementId", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(cut.Instance, "dashboard-assistant-button");
        typeof(MainLayout)
            .GetField("_assistantSidebarWasVisible", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(cut.Instance, true);

        Func<Task> hideAssistantSidebarAsync = aiContextProvider.HideAssistantSidebarAsync;
        await cut.InvokeAsync(hideAssistantSidebarAsync);

        cut.WaitForAssertion(() =>
        {
            Assert.Contains(JSInterop.Invocations, invocation =>
                invocation.Identifier == "focusElement" &&
                invocation.Arguments.Count == 1 &&
                string.Equals((string?)invocation.Arguments[0], "dashboard-assistant-button", StringComparison.Ordinal));
        });
    }

    [Fact]
    public async Task PromptLaunchedAssistantSidebarHide_DoesNotReusePreviousFocusTarget()
    {
        var aiContextProvider = new TestAIContextProvider();
        SetupMainLayoutServices(aiContextProvider: aiContextProvider);
        JSInterop.SetupVoid("focusElement", _ => true);

        var cut = RenderComponent<MainLayout>(builder =>
        {
            builder.Add(p => p.ViewportInformation, new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false));
        });

        typeof(MainLayout)
            .GetField("_assistantReturnFocusElementId", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(cut.Instance, "dashboard-assistant-button");
        typeof(MainLayout)
            .GetField("_assistantSidebarWasVisible", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(cut.Instance, true);

        Func<Task> launchPromptSidebarAsync = () => aiContextProvider.LaunchAssistantSidebarAsync(_ => Task.CompletedTask);
        await cut.InvokeAsync(launchPromptSidebarAsync);

        Func<Task> hideAssistantSidebarAsync = aiContextProvider.HideAssistantSidebarAsync;
        await cut.InvokeAsync(hideAssistantSidebarAsync);

        Assert.DoesNotContain(JSInterop.Invocations, invocation => invocation.Identifier == "focusElement");
    }

    [Fact]
    public async Task AssistantModalDialogClose_RestoresFocusToLaunchButton()
    {
        DialogParameters? capturedParameters = null;
        TestDialogService? dialogService = null;
        dialogService = new TestDialogService(onShowDialog: (_, parameters) =>
        {
            capturedParameters = parameters;
            return Task.FromResult<IDialogReference>(new DialogReference(parameters.Id, dialogService!));
        });
        var js = new RecordingJSRuntime();

        await AssistantModalDialog.OpenDialogAsync(dialogService, js, "Assistant", new AssistantDialogViewModel { Chat = null! }, "dashboard-assistant-button");

        Assert.NotNull(capturedParameters);

        await capturedParameters.OnDialogClosing.InvokeAsync(null!);

        Assert.Collection(js.Invocations,
            invocation =>
            {
                Assert.Equal("focusElement", invocation.Identifier);
                Assert.Collection(invocation.Arguments, argument => Assert.Equal("dashboard-assistant-button", argument));
            });
    }

    [Theory]
    [InlineData(true, "dashboard-assistant-button", "dashboard-navigation-button")]
    [InlineData(false, "dashboard-assistant-button", "dashboard-assistant-button")]
    [InlineData(false, null, null)]
    public void AssistantSidebarSwitchToModal_UsesVisibleLauncherAsReturnFocusTarget(bool openedForMobileView, string? returnFocusElementId, string? expectedReturnFocusElementId)
    {
        Assert.Equal(expectedReturnFocusElementId, AssistantSidebarDialog.GetReturnFocusElementId(openedForMobileView, returnFocusElementId));
    }

    [Theory]
    [InlineData(true, "dashboard-navigation-button", "dashboard-assistant-button")]
    [InlineData(false, "dashboard-navigation-button", "dashboard-navigation-button")]
    [InlineData(false, null, null)]
    public void AssistantModalSwitchToSidebar_UsesVisibleLauncherAsReturnFocusTarget(bool openedForMobileView, string? returnFocusElementId, string? expectedReturnFocusElementId)
    {
        Assert.Equal(expectedReturnFocusElementId, AssistantModalDialog.GetSidebarReturnFocusElementId(openedForMobileView, returnFocusElementId));
    }

    private void SetupMainLayoutServices(
        TestLocalStorage? localStorage = null,
        MessageService? messageService = null,
        Action<DashboardOptions>? configureOptions = null,
        IDialogService? dialogService = null,
        IAIContextProvider? aiContextProvider = null,
        IDashboardFeedbackDiagnosticProvider? feedbackDiagnosticProvider = null)
    {
        FluentUISetupHelpers.AddCommonDashboardServices(this, localStorage: localStorage, messageService: messageService);

        if (dialogService is not null)
        {
            Services.AddSingleton(dialogService);
        }

        if (aiContextProvider is not null)
        {
            Services.AddSingleton(aiContextProvider);
            if (aiContextProvider is IAssistantDisplayContext assistantDisplayContext)
            {
                Services.AddSingleton(assistantDisplayContext);
            }
        }

        if (feedbackDiagnosticProvider is not null)
        {
            Services.AddSingleton(feedbackDiagnosticProvider);
        }

        Services.AddOptions();
        Services.AddSingleton<IThemeResolver, TestThemeResolver>();
        Services.AddSingleton<IDashboardClient, TestDashboardClient>();
        Services.AddSingleton<ITooltipService, TooltipService>();
        Services.AddSingleton<IToastService, ToastService>();
        Services.Configure<DashboardOptions>(o =>
        {
            // Configure OTLP endpoint URLs so they can be parsed
            o.Otlp.GrpcEndpointUrl = "http://localhost:4317";
            o.Otlp.AuthMode = OtlpAuthMode.Unsecured;
            configureOptions?.Invoke(o);
            // Call TryParseOptions to populate parsed endpoint addresses
            o.Otlp.TryParseOptions(out _);
        });

        FluentUISetupHelpers.SetupFluentDialogProvider(this);
        FluentUISetupHelpers.SetupFluentOverflow(this);
        FluentUISetupHelpers.SetupFluentAnchor(this);
        FluentUISetupHelpers.SetupFluentButton(this);
        FluentUISetupHelpers.SetupFluentMenu(this);
        FluentUISetupHelpers.SetupFluentAnchoredRegion(this);
        FluentUISetupHelpers.SetupFluentDivider(this);

        var themeModule = JSInterop.SetupModule("/js/app-theme.js");

        JSInterop.SetupModule("window.registerGlobalKeydownListener", _ => true);
        JSInterop.SetupModule("window.registerOpenTextVisualizerOnClick", _ => true);

        JSInterop.Setup<BrowserInfo>("window.getBrowserInfo").SetResult(new BrowserInfo { TimeZone = "abc", UserAgent = "mozilla" });
    }

    private static bool TryGetOpenArguments(IReadOnlyList<object?> arguments, out string url, out string target)
    {
        if (arguments is [string directUrl, string directTarget, ..])
        {
            url = directUrl;
            target = directTarget;
            return true;
        }

        if (arguments is [object?[] { Length: 2 } nestedArguments] &&
            nestedArguments[0] is string nestedUrl &&
            nestedArguments[1] is string nestedTarget)
        {
            url = nestedUrl;
            target = nestedTarget;
            return true;
        }

        url = string.Empty;
        target = string.Empty;
        return false;
    }

    private sealed class TestDashboardFeedbackDiagnosticProvider(string doctorOutput, string additionalContext) : IDashboardFeedbackDiagnosticProvider
    {
        public int AppHostInfoRequestedCount { get; private set; }

        public int DoctorOutputCaptureCount { get; private set; }

        public string BuildAdditionalContext(bool includeAppHostInfo)
        {
            if (includeAppHostInfo)
            {
                AppHostInfoRequestedCount++;
            }

            return additionalContext;
        }

        public Task<string> CaptureAspireDoctorOutputAsync(CancellationToken cancellationToken)
        {
            DoctorOutputCaptureCount++;
            return Task.FromResult(doctorOutput);
        }
    }

    private sealed class WaitingDashboardFeedbackDiagnosticProvider : IDashboardFeedbackDiagnosticProvider
    {
        private readonly TaskCompletionSource<string> _doctorOutputTaskCompletionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string BuildAdditionalContext(bool includeAppHostInfo) => "- Posted from: Dashboard";

        public Task<string> CaptureAspireDoctorOutputAsync(CancellationToken cancellationToken) =>
            _doctorOutputTaskCompletionSource.Task.WaitAsync(cancellationToken);

        public void SetResult()
        {
            _doctorOutputTaskCompletionSource.TrySetResult("""{"sdk":"10.0.301"}""");
        }
    }

    private sealed class RecordingJSRuntime : IJSRuntime
    {
        public List<Invocation> Invocations { get; } = [];

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            Invocations.Add(new Invocation(identifier, args ?? []));
            return ValueTask.FromResult(default(TValue)!);
        }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            Invocations.Add(new Invocation(identifier, args ?? []));
            return ValueTask.FromResult(default(TValue)!);
        }

        public sealed record Invocation(string Identifier, object?[] Arguments);
    }
}
