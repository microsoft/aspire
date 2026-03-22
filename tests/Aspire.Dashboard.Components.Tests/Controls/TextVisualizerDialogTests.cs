// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using Aspire.Dashboard.Components.Dialogs;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Utils;
using Bunit;
using Microsoft.AspNetCore.Components.Web.Virtualization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
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

        var cut = SetUpDialog(out var dialogService);
        await dialogService.ShowDialogAsync<TextVisualizerDialog>(new TextVisualizerDialogViewModel(rawJson, string.Empty, false), []);

        var instance = cut.FindComponent<TextVisualizerDialog>().Instance;

        Assert.Equal(expectedJson, instance.TextVisualizerViewModel.FormattedText);
        Assert.Equal(DashboardUIHelpers.JsonFormat, instance.TextVisualizerViewModel.FormatKind);
        Assert.Equal([DashboardUIHelpers.JsonFormat, DashboardUIHelpers.PlaintextFormat], instance.EnabledOptions.ToImmutableSortedSet());
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

        var cut = SetUpDialog(out var dialogService);
        await dialogService.ShowDialogAsync<TextVisualizerDialog>(new TextVisualizerDialogViewModel(rawXml, string.Empty, false), []);
        cut.WaitForAssertion(() => Assert.True(cut.HasComponent<TextVisualizerDialog>()));

        var instance = cut.FindComponent<TextVisualizerDialog>().Instance;

        Assert.Equal(DashboardUIHelpers.XmlFormat, instance.TextVisualizerViewModel.FormatKind);
        Assert.Equal(expectedXml, instance.TextVisualizerViewModel.FormattedText);
        Assert.Equal([DashboardUIHelpers.PlaintextFormat, DashboardUIHelpers.XmlFormat], instance.EnabledOptions.ToImmutableSortedSet());

        // changing format works
        instance.ChangeFormat(DashboardUIHelpers.PlaintextFormat, rawXml);

        Assert.Equal(DashboardUIHelpers.PlaintextFormat, instance.TextVisualizerViewModel.FormatKind);
        Assert.Equal(rawXml, instance.TextVisualizerViewModel.FormattedText);
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

        var cut = SetUpDialog(out var dialogService);
        await dialogService.ShowDialogAsync<TextVisualizerDialog>(new TextVisualizerDialogViewModel(rawXml, string.Empty, false), []);
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

        var cut = SetUpDialog(out var dialogService);
        await dialogService.ShowDialogAsync<TextVisualizerDialog>(new TextVisualizerDialogViewModel(rawText, string.Empty, false), []);
        cut.WaitForAssertion(() => Assert.True(cut.HasComponent<TextVisualizerDialog>()));

        var instance = cut.FindComponent<TextVisualizerDialog>().Instance;

        Assert.Equal(DashboardUIHelpers.PlaintextFormat, instance.TextVisualizerViewModel.FormatKind);
        Assert.Equal(rawText, instance.TextVisualizerViewModel.FormattedText);
        Assert.Equal([DashboardUIHelpers.PlaintextFormat], instance.EnabledOptions.ToImmutableSortedSet());
    }

    [Fact]
    public async Task Render_TextVisualizerDialog_WithDifferentThemes_LineClassesChange()
    {
        var xml = @"<hello><!-- world --></hello>";
        var themeManager = new ThemeManager(new TestThemeResolver { EffectiveTheme = "Light" });
        var cut = SetUpDialog(out var dialogService, themeManager: themeManager);
        themeManager.EffectiveTheme = "Light";
        await dialogService.ShowDialogAsync<TextVisualizerDialog>(new TextVisualizerDialogViewModel(xml, string.Empty, false), []);
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

        var cut = SetUpDialog(out var dialogService);
        await dialogService.ShowDialogAsync<TextVisualizerDialog>(new TextVisualizerDialogViewModel(xml, string.Empty, false), []);
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

        var cut = SetUpDialog(out var dialogService, localStorage: localStorage);
        await dialogService.ShowDialogAsync<TextVisualizerDialog>(new TextVisualizerDialogViewModel(rawText, string.Empty, ContainsSecret: true), []);
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
        var cut = SetUpDialog(out var dialogService, localStorage: localStorage);
        await dialogService.ShowDialogAsync<TextVisualizerDialog>(new TextVisualizerDialogViewModel(rawText, string.Empty, ContainsSecret: true), []);
        cut.WaitForAssertion(() => Assert.False(cut.FindComponent<TextVisualizerDialog>().Instance.ShowSecretsWarning));

        cut.WaitForAssertion(() => Assert.False(cut.FindComponent<TextVisualizerDialog>().Instance.ShowSecretsWarning));
        Assert.False(cut.HasComponent<FluentMessageBar>());
        Assert.True(cut.HasComponent<Virtualize<StringLogLine>>());
    }

    [Fact]
    public async Task Render_TextVisualizerDialog_WithFixedFormat_UsesFixedFormatAndHidesDropdownAsync()
    {
        const string rawText = """export VAR=value""";

        var cut = SetUpDialog(out var dialogService);
        await dialogService.ShowDialogAsync<TextVisualizerDialog>(new TextVisualizerDialogViewModel(rawText, string.Empty, ContainsSecret: false, FixedFormat: DashboardUIHelpers.PropertiesFormat), []);
        cut.WaitForAssertion(() => Assert.True(cut.HasComponent<TextVisualizerDialog>()));

        var instance = cut.FindComponent<TextVisualizerDialog>().Instance;

        // Verify the fixed format is used
        Assert.Equal(DashboardUIHelpers.PropertiesFormat, instance.TextVisualizerViewModel.FormatKind);
        Assert.True(instance.HasFixedFormat);

        // Verify the format dropdown is not rendered
        Assert.Empty(cut.FindAll("#" + instance.GetType().GetField("_openSelectFormatButtonId", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(instance)));
    }

    [Fact]
    public async Task Render_TextVisualizerDialog_WithFixedJsonFormat_UsesJsonFormatAsync()
    {
        const string rawText = """{"key": "value"}""";

        var cut = SetUpDialog(out var dialogService);
        await dialogService.ShowDialogAsync<TextVisualizerDialog>(new TextVisualizerDialogViewModel(rawText, string.Empty, ContainsSecret: false, FixedFormat: DashboardUIHelpers.JsonFormat), []);
        cut.WaitForAssertion(() => Assert.True(cut.HasComponent<TextVisualizerDialog>()));

        var instance = cut.FindComponent<TextVisualizerDialog>().Instance;

        // Verify the fixed format is used
        Assert.Equal(DashboardUIHelpers.JsonFormat, instance.TextVisualizerViewModel.FormatKind);
        Assert.True(instance.HasFixedFormat);
    }

    private IRenderedFragment SetUpDialog(out IDialogService dialogService, ThemeManager? themeManager = null, TestLocalStorage? localStorage = null)
    {
        FluentUISetupHelpers.SetupDialogInfrastructure(this, themeManager: themeManager, localStorage: localStorage);

        var module = JSInterop.SetupModule("/Components/Controls/TextVisualizer.razor.js");
        module.SetupVoid();

        FluentUISetupHelpers.SetupFluentAnchoredRegion(this);
        FluentUISetupHelpers.SetupFluentMenu(this);

        var cut = FluentUISetupHelpers.RenderDialogProvider(this);

        dialogService = Services.GetRequiredService<IDialogService>();
        return cut;
    }
}
