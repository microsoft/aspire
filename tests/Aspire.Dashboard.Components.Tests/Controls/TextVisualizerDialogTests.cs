// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Components.Dialogs;
using Aspire.Dashboard.Components.Resize;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Tests;
using Aspire.Dashboard.Utils;
using Aspire.Tests.Shared;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web.Virtualization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Controls;

public class TextVisualizerDialogTests : DashboardTestContext
{
    [Fact]
    public async Task Render_TextVisualizerDialog_WithValidJson_FormatsJsonAsync()
    {
        var rawJson = """
                      // line comment
                      [
                          /* block comment */
                          1,
                          { "test": {    "nested": "value" } }
                      ]
                      """;

        var expectedJson = """
                           /* line comment*/
                           [
                             /* block comment */
                             1,
                             {
                               "test": {
                                 "nested": "value"
                               }
                             }
                           ]
                           """;

        var getCut = SetUpDialog(out var dialogService);
        await dialogService.ShowDialogAsync<TextVisualizerDialog>(new TextVisualizerDialogViewModel(rawJson, string.Empty, false), new DialogParameters());
        var cut = getCut();

        var instance = cut.FindComponent<TextVisualizerDialog>().Instance;

        Assert.Equal(expectedJson, instance.TextVisualizerViewModel.FormattedText);
        Assert.Equal(DashboardUIHelpers.JsonFormat, instance.TextVisualizerViewModel.FormatKind);
        Assert.Equal([DashboardUIHelpers.JsonFormat, DashboardUIHelpers.PlaintextFormat], instance.EnabledOptions.ToImmutableSortedSet());
        Assert.Single(cut.FindAll("fluent-dialog-body [slot='title']"));
        Assert.Single(cut.FindAll("fluent-dialog-body [slot='title'] svg"));
        Assert.Single(cut.FindAll("fluent-dialog-body [slot='title'] .dialog-format"));
        Assert.Single(cut.FindAll("fluent-dialog-body [slot='action'] .button-container"));
        Assert.Empty(cut.FindAll(".text-visualizer-container .button-container"));
        Assert.Empty(cut.FindAll("header"));
    }

    [Fact]
    public async Task Render_TextVisualizerDialog_WithValidXml_FormatsXml_CanChangeFormatAsync()
    {
        const string rawXml = """<parent><child>text<!-- comment --></child></parent>""";
        const string expectedXml =
            """
            <parent>
              <child>text<!-- comment --></child>
            </parent>
            """;

        var getCut = SetUpDialog(out var dialogService);
        await dialogService.ShowDialogAsync<TextVisualizerDialog>(new TextVisualizerDialogViewModel(rawXml, string.Empty, false), new DialogParameters());
        var cut = getCut();
        cut.WaitForAssertion(() => Assert.True(cut.HasComponent<TextVisualizerDialog>()));

        var instance = cut.FindComponent<TextVisualizerDialog>().Instance;

        Assert.Equal(DashboardUIHelpers.XmlFormat, instance.TextVisualizerViewModel.FormatKind);
        Assert.Equal(expectedXml, instance.TextVisualizerViewModel.FormattedText);
        Assert.Equal([DashboardUIHelpers.PlaintextFormat, DashboardUIHelpers.XmlFormat], instance.EnabledOptions.ToImmutableSortedSet());

        instance.ChangeFormat(DashboardUIHelpers.PlaintextFormat);

        Assert.Equal(DashboardUIHelpers.PlaintextFormat, instance.TextVisualizerViewModel.FormatKind);
        Assert.Equal(rawXml, instance.TextVisualizerViewModel.FormattedText);
    }

    [Fact]
    public async Task Render_TextVisualizerDialog_FormatPicker_UsesFluentSelectAndPreservesSelectionAfterParentRerenderAsync()
    {
        const string rawXml = """<parent><child>text<!-- comment --></child></parent>""";

        var content = new TextVisualizerDialogViewModel(rawXml, string.Empty, false);
        var getCut = SetUpDialog(out var dialogService);
        await dialogService.ShowDialogAsync<TextVisualizerDialog>(content, new DialogParameters());
        var cut = getCut();
        cut.WaitForAssertion(() => Assert.True(cut.HasComponent<TextVisualizerDialog>()));

        var formatSelect = Assert.Single(cut.FindComponents<FluentSelect<SelectViewModel<string>, SelectViewModel<string>>>());
        Assert.NotNull(formatSelect.Find("fluent-dropdown"));

        Assert.Equal(Aspire.Dashboard.Resources.Dialogs.TextVisualizerSelectFormatType, formatSelect.Instance.AriaLabel);
        Assert.Equal(DashboardUIHelpers.XmlFormat, formatSelect.Instance.Value?.Id);
        Assert.Null(formatSelect.Instance.OptionText!(null));
        Assert.False(formatSelect.Instance.OptionDisabled!(null));

        var formatOptions = formatSelect.Instance.Items ?? throw new InvalidOperationException("Expected format options.");
        var plaintextOption = formatOptions.Single(o => o.Id == DashboardUIHelpers.PlaintextFormat);
        await formatSelect.InvokeAsync(() => formatSelect.Instance.ValueChanged.InvokeAsync(plaintextOption));

        cut.WaitForAssertion(() =>
        {
            var dialog = cut.FindComponent<TextVisualizerDialog>().Instance;
            Assert.Equal(DashboardUIHelpers.PlaintextFormat, dialog.TextVisualizerViewModel.FormatKind);
            Assert.Equal(rawXml, dialog.TextVisualizerViewModel.FormattedText);
            Assert.Equal(DashboardUIHelpers.PlaintextFormat, cut.FindComponent<FluentSelect<SelectViewModel<string>, SelectViewModel<string>>>().Instance.Value?.Id);
            Assert.Single(cut.FindAll(".text-visualizer-unformatted"));
        });

        cut.FindComponent<TextVisualizerDialog>().Render(parameters => parameters.Add(p => p.Content, content));

        cut.WaitForAssertion(() =>
        {
            var dialog = cut.FindComponent<TextVisualizerDialog>().Instance;
            Assert.Equal(DashboardUIHelpers.PlaintextFormat, dialog.TextVisualizerViewModel.FormatKind);
            Assert.Equal(rawXml, dialog.TextVisualizerViewModel.FormattedText);
            Assert.Equal(DashboardUIHelpers.PlaintextFormat, cut.FindComponent<FluentSelect<SelectViewModel<string>, SelectViewModel<string>>>().Instance.Value?.Id);
        });
    }

    [Fact]
    public void Render_TextVisualizer_DisplayUnformatted_AddsUnformattedClass()
    {
        SetUpDialog(out _);

        var cut = Render<TextVisualizer>(parameters => parameters
            .Add(p => p.ViewModel, new TextVisualizerViewModel("""{"value":1}""", indentText: false))
            .Add(p => p.DisplayUnformatted, true)
            .Add(p => p.Virtualize, false));

        Assert.Single(cut.FindAll(".text-visualizer-unformatted"));
    }

    [Fact]
    public async Task Render_TextVisualizerDialog_WithValidXml_FormatsXmlWithDoctypeAsync()
    {
        const string rawXml = """<?xml version="1.0" encoding="utf-16"?><test>text content</test>""";
        const string expectedXml =
            """
            <?xml version="1.0" encoding="utf-16"?>
            <test>text content</test>
            """;

        var getCut = SetUpDialog(out var dialogService);
        await dialogService.ShowDialogAsync<TextVisualizerDialog>(new TextVisualizerDialogViewModel(rawXml, string.Empty, false), new DialogParameters());
        var cut = getCut();
        cut.WaitForAssertion(() => Assert.True(cut.HasComponent<TextVisualizerDialog>()));

        var instance = cut.FindComponent<TextVisualizerDialog>().Instance;

        Assert.Equal(DashboardUIHelpers.XmlFormat, instance.TextVisualizerViewModel.FormatKind);
        Assert.Equal(expectedXml, instance.TextVisualizerViewModel.FormattedText);
        Assert.Equal([DashboardUIHelpers.PlaintextFormat, DashboardUIHelpers.XmlFormat], instance.EnabledOptions.ToImmutableSortedSet());
    }

    [Fact]
    public async Task Render_TextVisualizerDialog_WithInvalidJson_FormatsPlaintextAsync()
    {
        const string rawText = """{{{{{{"test": 4}""";

        var getCut = SetUpDialog(out var dialogService);
        await dialogService.ShowDialogAsync<TextVisualizerDialog>(new TextVisualizerDialogViewModel(rawText, string.Empty, false), new DialogParameters());
        var cut = getCut();
        cut.WaitForAssertion(() => Assert.True(cut.HasComponent<TextVisualizerDialog>()));

        var instance = cut.FindComponent<TextVisualizerDialog>().Instance;

        Assert.Equal(DashboardUIHelpers.PlaintextFormat, instance.TextVisualizerViewModel.FormatKind);
        Assert.Equal(rawText, instance.TextVisualizerViewModel.FormattedText);
        Assert.Equal([DashboardUIHelpers.MarkdownFormat, DashboardUIHelpers.PlaintextFormat, DashboardUIHelpers.SqlFormat], instance.EnabledOptions.ToImmutableSortedSet());
    }

    [Fact]
    public async Task Render_TextVisualizerDialog_WithPlaintextUrl_RendersClickableLinkAsync()
    {
        const string rawText = "See https://aka.ms/aspire/container-runtime-unhealthy for more information.";

        var getCut = SetUpDialog(out var dialogService);
        await dialogService.ShowDialogAsync<TextVisualizerDialog>(new TextVisualizerDialogViewModel(rawText, string.Empty, false), new DialogParameters());
        var cut = getCut();
        cut.WaitForAssertion(() => Assert.True(cut.HasComponent<TextVisualizerDialog>()));

        var link = cut.Find("a[href='https://aka.ms/aspire/container-runtime-unhealthy']");
        Assert.Equal("_blank", link.GetAttribute("target"));
    }

    [Fact]
    public async Task Render_TextVisualizerDialog_WithDifferentThemes_LineClassesChange()
    {
        var xml = @"<hello><!-- world --></hello>";
        var themeManager = new ThemeManager(new TestThemeResolver { EffectiveTheme = "Light" });
        var getCut = SetUpDialog(out var dialogService, themeManager: themeManager);
        themeManager.EffectiveTheme = "Light";
        await dialogService.ShowDialogAsync<TextVisualizerDialog>(new TextVisualizerDialogViewModel(xml, string.Empty, false), new DialogParameters());
        var cut = getCut();
        cut.WaitForAssertion(() => Assert.True(cut.HasComponent<TextVisualizerDialog>()));

        Assert.NotEmpty(cut.FindAll(".theme-a11y-light-min"));

        themeManager.EffectiveTheme = "Dark";
        var instance = cut.FindComponent<TextVisualizerDialog>();
        instance.Render();

        Assert.NotEmpty(cut.FindAll(".theme-a11y-dark-min"));
    }

    [Fact]
    public async Task Render_TextVisualizerDialog_ResolveTheme_LineClassesChange()
    {
        var xml = @"<hello><!-- world --></hello>";

        var getCut = SetUpDialog(out var dialogService);
        await dialogService.ShowDialogAsync<TextVisualizerDialog>(new TextVisualizerDialogViewModel(xml, string.Empty, false), new DialogParameters());
        var cut = getCut();
        cut.WaitForAssertion(() => Assert.True(cut.HasComponent<TextVisualizerDialog>()));

        Assert.NotEmpty(cut.FindAll(".theme-a11y-dark-min"));
    }

    [Fact]
    public async Task Render_TextVisualizerDialog_WithSecret_ShowsWarningFirstOpenAsync()
    {
        const string rawText = """my text with a secret""";

        bool? secretsWarningAcknowledged = null;
        var localStorage = new TestLocalStorage { OnSetUnprotectedAsync = (_, o) =>
            {
                if (o is TextVisualizerDialog.TextVisualizerDialogSettings s)
                {
                    secretsWarningAcknowledged = s.SecretsWarningAcknowledged;
                }
            }
        };

        var getCut = SetUpDialog(out var dialogService, localStorage: localStorage);
        await dialogService.ShowDialogAsync<TextVisualizerDialog>(new TextVisualizerDialogViewModel(rawText, string.Empty, ContainsSecret: true), new DialogParameters());
        var cut = getCut();
        cut.WaitForAssertion(() => Assert.True(cut.HasComponent<TextVisualizerDialog>()));

        Assert.Single(cut.FindAll(".block-warning"));
        Assert.False(cut.HasComponent<Virtualize<StringLogLine>>());

        cut.Find(".text-visualizer-unmask-content").Click();

        cut.WaitForAssertion(() => Assert.False(cut.FindComponent<TextVisualizerDialog>().Instance.ShowSecretsWarning));
        Assert.Empty(cut.FindAll(".block-warning"));
        Assert.True(cut.HasComponent<Virtualize<StringLogLine>>());
        Assert.True(secretsWarningAcknowledged);
    }

    [Fact]
    public async Task Render_TextVisualizerDialog_WithSecret_DoesNotShowWarningIfSetLocallyFirstOpenAsync()
    {
        const string rawText = """my text with a secret""";

        var localStorage = new TestLocalStorage();
        localStorage.OnGetUnprotectedAsync = _ => new ValueTuple<bool, object>(true, new TextVisualizerDialog.TextVisualizerDialogSettings(SecretsWarningAcknowledged: true));
        var getCut = SetUpDialog(out var dialogService, localStorage: localStorage);
        await dialogService.ShowDialogAsync<TextVisualizerDialog>(new TextVisualizerDialogViewModel(rawText, string.Empty, ContainsSecret: true), new DialogParameters());
        var cut = getCut();
        cut.WaitForAssertion(() => Assert.False(cut.FindComponent<TextVisualizerDialog>().Instance.ShowSecretsWarning));

        cut.WaitForAssertion(() => Assert.False(cut.FindComponent<TextVisualizerDialog>().Instance.ShowSecretsWarning));
        Assert.False(cut.HasComponent<FluentMessageBar>());
        Assert.True(cut.HasComponent<Virtualize<StringLogLine>>());
    }

    [Fact]
    public async Task Render_TextVisualizerDialog_WithSecretAndAsyncSettingsLoad_RendersActionsAfterInitializationAsync()
    {
        const string rawText = """my text with a secret""";

        var localStorage = new TestLocalStorage
        {
            OnBeforeGetUnprotectedAsync = async _ => await Task.Yield()
        };
        var getCut = SetUpDialog(out var dialogService, localStorage: localStorage);

        await dialogService.ShowDialogAsync<TextVisualizerDialog>(new TextVisualizerDialogViewModel(rawText, string.Empty, ContainsSecret: true), new DialogParameters());
        var cut = getCut();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".button-container")));
    }

    [Fact]
    public async Task Render_TextVisualizerDialog_WithFixedFormat_UsesFixedFormatAndHidesDropdownAsync()
    {
        const string rawText = """export VAR=value""";

        var getCut = SetUpDialog(out var dialogService);
        await dialogService.ShowDialogAsync<TextVisualizerDialog>(new TextVisualizerDialogViewModel(rawText, string.Empty, ContainsSecret: false, FixedFormat: DashboardUIHelpers.PropertiesFormat), new DialogParameters());
        var cut = getCut();
        cut.WaitForAssertion(() => Assert.True(cut.HasComponent<TextVisualizerDialog>()));

        var instance = cut.FindComponent<TextVisualizerDialog>().Instance;

        // Verify the fixed format is used
        Assert.Equal(DashboardUIHelpers.PropertiesFormat, instance.TextVisualizerViewModel.FormatKind);
        Assert.True(instance.HasFixedFormat);

        // Verify the format dropdown is not rendered
        Assert.Empty(cut.FindComponents<FluentSelect<SelectViewModel<string>, SelectViewModel<string>>>());
    }

    [Fact]
    public async Task Render_TextVisualizerDialog_WithFixedJsonFormat_UsesJsonFormatAsync()
    {
        const string rawText = """{"key": "value"}""";

        var getCut = SetUpDialog(out var dialogService);
        await dialogService.ShowDialogAsync<TextVisualizerDialog>(new TextVisualizerDialogViewModel(rawText, string.Empty, ContainsSecret: false, FixedFormat: DashboardUIHelpers.JsonFormat), new DialogParameters());
        var cut = getCut();
        cut.WaitForAssertion(() => Assert.True(cut.HasComponent<TextVisualizerDialog>()));

        var instance = cut.FindComponent<TextVisualizerDialog>().Instance;

        // Verify the fixed format is used
        Assert.Equal(DashboardUIHelpers.JsonFormat, instance.TextVisualizerViewModel.FormatKind);
        Assert.Equal(
            """
            {
              "key": "value"
            }
            """,
            instance.TextVisualizerViewModel.FormattedText);
        Assert.True(instance.HasFixedFormat);
    }

    [Fact]
    public async Task Render_TextVisualizerDialog_MermaidSource_CanCopyAndDownloadAsync()
    {
        const string mermaid = "flowchart LR\n    resource0[\"api\"]\n    resource1[\"cache\"]\n    resource0 --> resource1\n";
        var getCut = SetUpDialog(out var dialogService);
        await TextVisualizerDialog.OpenDialogAsync(new OpenTextVisualizerDialogOptions
        {
            DialogService = dialogService,
            ValueDescription = "Export as Mermaid",
            Value = mermaid,
            DownloadFileName = "resources.mmd",
            FixedFormat = DashboardUIHelpers.PlaintextFormat
        });
        var cut = getCut();
        cut.WaitForAssertion(() => Assert.True(cut.HasComponent<TextVisualizerDialog>()));

        Assert.Equal(mermaid, cut.FindComponent<TextVisualizer>().Instance.ViewModel.FormattedText);
        Assert.Empty(cut.FindComponents<FluentSelect<SelectViewModel<string>, SelectViewModel<string>>>());
        Assert.Equal(mermaid, cut.Find("[data-copybutton='true']").GetAttribute("data-text"));

        string? downloadedText = null;
        var download = JSInterop.SetupVoid("downloadStreamAsFile", invocation =>
        {
            var stream = Assert.IsType<DotNetStreamReference>(invocation.Arguments[1]);
            using var reader = new StreamReader(stream.Stream, leaveOpen: true);
            downloadedText = reader.ReadToEnd();
            return true;
        });
        download.SetVoidResult();

        var downloadButton = Assert.Single(cut.FindComponents<FluentButton>(),
            button => button.Instance.Title == Aspire.Dashboard.Resources.ControlsStrings.Download);
        await downloadButton.InvokeAsync(downloadButton.Instance.OnClick.InvokeAsync);

        Assert.Equal("resources.mmd", Assert.Single(download.Invocations).Arguments[0]);
        Assert.Equal(mermaid, downloadedText);
    }

    [Theory]
    [InlineData(null, DashboardUIHelpers.PlaintextFormat)]
    [InlineData(DashboardUIHelpers.SqlFormat, DashboardUIHelpers.SqlFormat)]
    public async Task Render_TextVisualizerDialog_SqlOption_CanSelectAndPreservesSelectionAsync(string? fallbackFormat, string expectedFormat)
    {
        const string query = "SELECT * FROM Products WHERE Id = @id";
        var getCut = SetUpDialog(out var dialogService);
        var content = new TextVisualizerDialogViewModel(query, "commandText", false, FallbackFormat: fallbackFormat);
        await dialogService.ShowDialogAsync<TextVisualizerDialog>(content, new DialogParameters());
        var cut = getCut();
        var dialog = cut.FindComponent<TextVisualizerDialog>();
        var select = cut.FindComponent<FluentSelect<SelectViewModel<string>, SelectViewModel<string>>>();

        Assert.Equal(expectedFormat, dialog.Instance.TextVisualizerViewModel.FormatKind);
        Assert.Equal(query, dialog.Instance.TextVisualizerViewModel.FormattedText);
        var options = select.Instance.Items ?? throw new InvalidOperationException("Expected format options.");
        var sqlOption = options.Single(o => o.Id == DashboardUIHelpers.SqlFormat);
        var markdownOption = options.Single(o => o.Id == DashboardUIHelpers.MarkdownFormat);
        Assert.Equal("SQL", sqlOption.Name);
        Assert.False(select.Instance.OptionDisabled!(sqlOption));
        Assert.False(select.Instance.OptionDisabled!(markdownOption));

        await select.InvokeAsync(() => select.Instance.ValueChanged.InvokeAsync(sqlOption));
        dialog.Render(parameters => parameters.Add(p => p.Content, content));

        Assert.Equal(DashboardUIHelpers.SqlFormat, dialog.Instance.TextVisualizerViewModel.FormatKind);
        Assert.Equal(DashboardUIHelpers.SqlFormat, select.Instance.Value?.Id);
        Assert.Equal(query, dialog.Instance.TextVisualizerViewModel.FormattedText);

        var plaintextOption = options.Single(o => o.Id == DashboardUIHelpers.PlaintextFormat);
        await select.InvokeAsync(() => select.Instance.ValueChanged.InvokeAsync(plaintextOption));
        Assert.Equal(DashboardUIHelpers.PlaintextFormat, dialog.Instance.TextVisualizerViewModel.FormatKind);
        Assert.Equal(query, dialog.Instance.TextVisualizerViewModel.FormattedText);
    }

    [Theory]
    [InlineData("""{"query":"SELECT 1"}""", DashboardUIHelpers.JsonFormat)]
    [InlineData("<query>SELECT 1</query>", DashboardUIHelpers.XmlFormat)]
    public async Task Render_TextVisualizerDialog_SqlFallback_JsonAndXmlDisableSqlAndMarkdownAsync(string text, string expectedFormat)
    {
        var getCut = SetUpDialog(out var dialogService);
        await TextVisualizerDialog.OpenDialogAsync(new OpenTextVisualizerDialogOptions
        {
            DialogService = dialogService,
            Value = text,
            ValueDescription = "commandText",
            FallbackFormat = DashboardUIHelpers.SqlFormat
        });
        var cut = getCut();
        var dialog = cut.FindComponent<TextVisualizerDialog>().Instance;
        var select = cut.FindComponent<FluentSelect<SelectViewModel<string>, SelectViewModel<string>>>();
        var options = select.Instance.Items ?? throw new InvalidOperationException("Expected format options.");

        Assert.Equal(expectedFormat, dialog.TextVisualizerViewModel.FormatKind);
        Assert.True(select.Instance.OptionDisabled!(options.Single(o => o.Id == DashboardUIHelpers.SqlFormat)));
        Assert.True(select.Instance.OptionDisabled!(options.Single(o => o.Id == DashboardUIHelpers.MarkdownFormat)));
    }

    [Fact]
    public async Task Render_GridValue_SqlHint_ReachesVisualizerAsync()
    {
        var getCut = SetUpDialog(out var dialogService);

        var gridValue = Render<GridValue>(parameters => parameters
            .Add(p => p.Value, "SELECT 1")
            .Add(p => p.ValueDescription, "commandText")
            .Add(p => p.TextVisualizerFallbackFormat, DashboardUIHelpers.SqlFormat));
        await gridValue.FindComponent<FluentButton>().InvokeAsync(() => gridValue.FindComponent<FluentButton>().Instance.OnClick.InvokeAsync());

        Assert.Equal(DashboardUIHelpers.SqlFormat, getCut().FindComponent<TextVisualizerDialog>().Instance.TextVisualizerViewModel.FormatKind);
    }

    [Fact]
    public void Render_TextVisualizer_Sql_EmitsHighlightLanguageAndEncodedContent()
    {
        SetUpDialog(out _);
        var cut = Render<TextVisualizer>(parameters => parameters
            .Add(p => p.ViewModel, new TextVisualizerViewModel("SELECT '<script>'", indentText: true, fallbackFormat: DashboardUIHelpers.SqlFormat))
            .Add(p => p.Virtualize, false));

        var line = Assert.Single(cut.FindAll(".highlight-line.language-sql"));
        Assert.Equal("sql", line.GetAttribute("data-language"));
        Assert.Equal("SELECT '<script>'", line.GetAttribute("data-content"));
        Assert.Equal("SELECT '<script>'", line.TextContent.Trim());
        Assert.Empty(line.QuerySelectorAll("script"));
    }

    private Func<IRenderedComponent<IComponent>> SetUpDialog(out DashboardDialogService dialogService, ThemeManager? themeManager = null, TestLocalStorage? localStorage = null)
    {
        FluentUISetupHelpers.SetupDialogInfrastructure(this, themeManager: themeManager, localStorage: localStorage);

        var module = JSInterop.SetupModule("/Components/Controls/TextVisualizer.razor.js");
        module.SetupVoid();

        FluentUISetupHelpers.SetupFluentAnchoredRegion(this);
        FluentUISetupHelpers.SetupFluentInputLabel(this);
        FluentUISetupHelpers.SetupFluentList(this);
        FluentUISetupHelpers.SetupFluentMenu(this);

        IRenderedComponent<IComponent>? cut = null;
        TestDialogService? testDialogService = null;
        testDialogService = new TestDialogService((content, _) =>
        {
            cut = Render<CascadingValue<IDialogInstance>>(builder =>
            {
                builder.Add(p => p.Value, testDialogService!.LastInstance!);
                builder.AddChildContent<TextVisualizerDialog>(childBuilder =>
                {
                    childBuilder.Add(p => p.Content, Assert.IsType<TextVisualizerDialogViewModel>(content));
                });
            });
            return Task.CompletedTask;
        });
        Services.RemoveAll<IDialogService>();
        Services.AddSingleton<IDialogService>(testDialogService);

        Services.RemoveAll<DashboardDialogService>();
        Services.AddSingleton<DashboardDialogService>(services => new DashboardDialogService(
            testDialogService,
            new TestStringLocalizer<Aspire.Dashboard.Resources.Dialogs>(),
            services.GetRequiredService<DimensionManager>()));
        dialogService = Services.GetRequiredService<DashboardDialogService>();
        return () => cut ?? throw new InvalidOperationException("The dialog was not rendered.");
    }
}
