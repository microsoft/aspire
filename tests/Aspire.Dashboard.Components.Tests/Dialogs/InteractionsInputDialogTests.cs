// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Dialogs;
using Aspire.Dashboard.Components.Resize;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.Interaction;
using Aspire.Dashboard.Tests;
using Aspire.Dashboard.Tests.Shared;
using Aspire.DashboardService.Proto.V1;
using Aspire.Tests.Shared;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Dialogs;

[UseCulture("en-US")]
public sealed class InteractionsInputDialogTests : DashboardTestContext
{
    [Fact]
    public async Task Render_FileUsesFallbackPlaceholderAndScopedBrowseLabel()
    {
        var getCut = SetUpDialog(out var dialogService);
        var interaction = new WatchInteractionsResponseUpdate
        {
            InteractionId = 1,
            InputsDialog = new InteractionInputsDialog()
        };
        interaction.InputsDialog.InputItems.Add(new InteractionInput
        {
            Name = "artifact",
            Label = "Artifact",
            InputType = InputType.File,
            Placeholder = string.Empty
        });
        var viewModel = new InteractionsInputsDialogViewModel
        {
            Interaction = interaction,
            Message = string.Empty,
            DashboardClient = new TestDashboardClient(),
            OnSubmitCallback = (_, _) => Task.CompletedTask
        };

        await dialogService.ShowDialogAsync<InteractionsInputDialog>(viewModel, new DialogParameters
        {
            Title = "Upload"
        });
        var cut = getCut();

        cut.WaitForAssertion(() =>
        {
            var browseButton = cut.Find("fluent-button[aria-label='Artifact']");
            Assert.NotNull(browseButton.Id);
            Assert.EndsWith("-FileUploadButton", browseButton.Id);
        });
    }

    [Fact]
    public async Task Render_SecretRevealButton_IsKeyboardFocusable()
    {
        var getCut = SetUpDialog(out var dialogService);

        await dialogService.ShowDialogAsync<InteractionsInputDialog>(CreateSecretTextViewModel(), new DialogParameters
        {
            Title = "Credentials"
        });
        var cut = getCut();

        cut.WaitForAssertion(() =>
        {
            var revealButton = cut.Find(".secret-text-toggle-button");
            Assert.Null(revealButton.GetAttribute("tabindex"));
            Assert.Contains("aspire-icon-button", revealButton.ClassList);
            Assert.Contains("aspire-input", cut.Find("fluent-field").ClassList);
        });
    }

    [Fact]
    public async Task Render_ActionButtons_DisplaySpecifiedText()
    {
        var getCut = SetUpDialog(out var dialogService);

        await dialogService.ShowDialogAsync<InteractionsInputDialog>(CreateSecretTextViewModel(), new DialogParameters
        {
            Title = "Credentials",
            PrimaryAction = "Continue",
            SecondaryAction = "Go back",
            UseCustomFooter = true
        });
        var cut = getCut();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("page-dialog-body", cut.Find("fluent-dialog-body").ClassList);
            var buttons = cut.FindAll("fluent-dialog-body [slot='action'] footer fluent-button");
            Assert.Collection(
                buttons,
                button =>
                {
                    Assert.Equal("Continue", button.TextContent.Trim());
                    Assert.Contains("aspire-button", button.ClassList);
                },
                button =>
                {
                    Assert.Equal("Go back", button.TextContent.Trim());
                    Assert.Contains("aspire-button", button.ClassList);
                });

            Assert.Empty(cut.FindAll("fluent-dialog-body + footer"));
        });
    }

    [Fact]
    public async Task Render_CustomChoiceTypingFiltersAndHighlightsOptions()
    {
        var getCut = SetUpDialog(out var dialogService);
        var viewModel = CreateChoiceViewModel(allowCustomChoice: true);

        await dialogService.ShowDialogAsync<InteractionsInputDialog>(viewModel, new DialogParameters
        {
            Title = "Choose a color"
        });
        var cut = getCut();
        var combobox = cut.Find("fluent-dropdown[type='combobox']");
        Assert.DoesNotContain("TODO: Restore Immediate/ImmediateDelay", cut.Markup);

        await combobox.InputAsync(new ChangeEventArgs { Value = "blu" });

        Assert.Equal("blu", viewModel.Inputs[0].Value);
        var option = Assert.Single(combobox.QuerySelectorAll("fluent-option"));
        Assert.Equal("Blue", option.GetAttribute("text"));
        Assert.Equal("Blu", Assert.Single(option.QuerySelectorAll("mark")).TextContent);

        var component = Assert.Single(cut.FindComponents<FluentCombobox<SelectViewModel<string>, SelectViewModel<string>>>());
        Assert.Null(component.Instance.OptionText!(null));
        await component.InvokeAsync(() => component.Instance.ValueChanged.InvokeAsync(null));
        Assert.Equal(string.Empty, viewModel.Inputs[0].Value);
    }

    [Theory]
    [InlineData("blue", "Blue")]
    [InlineData("purple", "purple")]
    public async Task Render_CustomChoiceExistingValueInitializesText(string value, string expectedText)
    {
        var getCut = SetUpDialog(out var dialogService);
        var viewModel = CreateChoiceViewModel(allowCustomChoice: true, value);

        await dialogService.ShowDialogAsync<InteractionsInputDialog>(viewModel, new DialogParameters
        {
            Title = "Choose a color"
        });
        var cut = getCut();

        var component = Assert.Single(cut.FindComponents<FluentCombobox<SelectViewModel<string>, SelectViewModel<string>>>());
        Assert.Equal(value, component.Instance.Value?.Id);
        Assert.Contains(JSInterop.Invocations, invocation =>
            invocation.Identifier == "Microsoft.FluentUI.Blazor.Components.Select.Initialize" &&
            invocation.Arguments.Count == 2 &&
            Equals(invocation.Arguments[1], expectedText));
    }

    [Fact]
    public async Task Render_ChoiceCallbacksAcceptNullOption()
    {
        var getCut = SetUpDialog(out var dialogService);

        var viewModel = CreateChoiceViewModel(allowCustomChoice: false);
        await dialogService.ShowDialogAsync<InteractionsInputDialog>(viewModel, new DialogParameters
        {
            Title = "Choose a color"
        });
        var cut = getCut();

        var component = Assert.Single(cut.FindComponents<FluentSelect<SelectViewModel<string>, string>>());
        Assert.Null(component.Instance.OptionValue!(null));
        Assert.Null(component.Instance.OptionText!(null));
        await component.InvokeAsync(() => component.Instance.ValueChanged.InvokeAsync(null));
        Assert.Equal(string.Empty, viewModel.Inputs[0].Value);
    }

    [Theory]
    [InlineData(InteractionHelpers.MaxFileCount, true)]
    [InlineData(InteractionHelpers.MaxFileCount + 1, false)]
    public async Task Render_MultipleFileSelection_ValidatesMaximumFileCount(int fileCount, bool expectedAccepted)
    {
        var getCut = SetUpDialog(out var dialogService);
        var interaction = new WatchInteractionsResponseUpdate
        {
            InteractionId = 1,
            InputsDialog = new InteractionInputsDialog()
        };
        interaction.InputsDialog.InputItems.Add(new InteractionInput
        {
            Name = "artifacts",
            Label = "Artifacts",
            InputType = InputType.File,
            AllowMultipleFiles = true
        });
        var viewModel = new InteractionsInputsDialogViewModel
        {
            Interaction = interaction,
            Message = string.Empty,
            DashboardClient = new TestDashboardClient(),
            OnSubmitCallback = (_, _) => Task.CompletedTask
        };
        await dialogService.ShowDialogAsync<InteractionsInputDialog>(viewModel, new DialogParameters
        {
            Title = "Upload"
        });
        var cut = getCut();
        var files = Enumerable.Range(0, fileCount)
            .Select(i => (IBrowserFile)new TestBrowserFile($"file-{i}.txt"))
            .ToArray();
        var inputFile = cut.FindComponent<FluentInputFile>();
        var args = new InputFileChangeEventArgs(files);

        if (expectedAccepted)
        {
            await cut.InvokeAsync(() => inputFile.Instance.OnInputFileChange.InvokeAsync(args));

            cut.WaitForAssertion(() => Assert.Equal(fileCount, cut.FindAll(".uploaded-file-container").Count));
        }
        else
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => cut.InvokeAsync(() => inputFile.Instance.OnInputFileChange.InvokeAsync(args)));

            Assert.Contains(InteractionHelpers.MaxFileCount.ToString(), exception.Message, StringComparison.Ordinal);
        }
    }

    private Func<IRenderedFragment> SetUpDialog(out DashboardDialogService dialogService)
    {
        FluentUISetupHelpers.SetupDialogInfrastructure(this);
        FluentUISetupHelpers.SetupFluentInputLabel(this);
        FluentUISetupHelpers.SetupFluentTextField(this);
        FluentUISetupHelpers.SetupFluentButton(this);
        FluentUISetupHelpers.SetupFluentInputFile(this);
        FluentUISetupHelpers.SetupFluentList(this);
        FluentUISetupHelpers.SetupFluentCombobox(this);

        var module = JSInterop.SetupModule("./Components/Dialogs/InteractionsInputDialog.razor.js");
        module.SetupVoid("togglePasswordVisibility", _ => true);

        IRenderedFragment? cut = null;
        TestDialogService? testDialogService = null;
        testDialogService = new TestDialogService((content, _) =>
        {
            cut = RenderComponent<CascadingValue<IDialogInstance>>(builder =>
            {
                builder.Add(p => p.Value, testDialogService!.LastInstance!);
                builder.AddChildContent<InteractionsInputDialog>(childBuilder =>
                {
                    childBuilder.Add(p => p.Content, Assert.IsType<InteractionsInputsDialogViewModel>(content));
                });
            });
            return Task.CompletedTask;
        });
        Services.RemoveAll<IDialogService>();
        Services.AddSingleton<IDialogService>(testDialogService);

        dialogService = new DashboardDialogService(
            testDialogService,
            new TestStringLocalizer<Aspire.Dashboard.Resources.Dialogs>(),
            Services.GetRequiredService<DimensionManager>());
        return () => cut ?? throw new InvalidOperationException("The dialog was not rendered.");
    }

    private static InteractionsInputsDialogViewModel CreateSecretTextViewModel()
    {
        var interaction = new WatchInteractionsResponseUpdate
        {
            InteractionId = 1,
            InputsDialog = new InteractionInputsDialog()
        };
        interaction.InputsDialog.InputItems.Add(new InteractionInput
        {
            Name = "password",
            Label = "Password",
            InputType = InputType.SecretText
        });

        return new InteractionsInputsDialogViewModel
        {
            Interaction = interaction,
            Message = string.Empty,
            DashboardClient = new TestDashboardClient(),
            OnSubmitCallback = (_, _) => Task.CompletedTask
        };
    }

    private static InteractionsInputsDialogViewModel CreateChoiceViewModel(bool allowCustomChoice, string value = "")
    {
        var interaction = new WatchInteractionsResponseUpdate
        {
            InteractionId = 1,
            InputsDialog = new InteractionInputsDialog()
        };
        var input = new InteractionInput
        {
            Name = "color",
            Label = "Color",
            InputType = InputType.Choice,
            AllowCustomChoice = allowCustomChoice,
            Value = value
        };
        input.Options.Add("red", "Red");
        input.Options.Add("blue", "Blue");
        interaction.InputsDialog.InputItems.Add(input);

        return new InteractionsInputsDialogViewModel
        {
            Interaction = interaction,
            Message = string.Empty,
            DashboardClient = new TestDashboardClient(),
            OnSubmitCallback = (_, _) => Task.CompletedTask
        };
    }

    private sealed class TestBrowserFile(string name) : IBrowserFile
    {
        public string Name { get; } = name;
        public DateTimeOffset LastModified { get; } = DateTimeOffset.UnixEpoch;
        public long Size => 0;
        public string ContentType => "text/plain";

        public Stream OpenReadStream(long maxAllowedSize = 512000, CancellationToken cancellationToken = default) => new MemoryStream();
    }
}
