// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Extensions;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Aspire.Dashboard.Components.Dialogs;

public partial class TextVisualizerDialog : ComponentBase
{
    private readonly string _copyButtonId = $"copy-{Guid.NewGuid():N}";
    private readonly string _openSelectFormatButtonId = $"select-format-{Guid.NewGuid():N}";

    private List<SelectViewModel<string>> _options = null!;
    private string? _selectedFormat;
    private bool _isLoading = true;
    internal TextVisualizerViewModel TextVisualizerViewModel { get; set; } = default!;

    public HashSet<string?> EnabledOptions { get; } = [];
    internal bool? ShowSecretsWarning { get; private set; }

    /// <summary>
    /// Returns true if the dialog has a fixed format that cannot be changed by the user.
    /// </summary>
    internal bool HasFixedFormat => Content.FixedFormat is not null;

    [Parameter, EditorRequired]
    public required TextVisualizerDialogViewModel Content { get; set; }

    [Inject]
    public required ILocalStorage LocalStorage { get; init; }

    [Inject]
    public required IJSRuntime JS { get; init; }

    protected override async Task OnInitializedAsync()
    {
        // We need to make users perform an explicit action once before being able to see secret values
        // We do this by making them agree to a warning in the text visualizer dialog.
        if (Content.ContainsSecret)
        {
            var settingsResult = await LocalStorage.GetUnprotectedAsync<TextVisualizerDialogSettings>(BrowserStorageKeys.TextVisualizerDialogSettings);
            ShowSecretsWarning = settingsResult.Value is not { SecretsWarningAcknowledged: true };
        }

        // Don't display content until it is loaded.
        // This is required because rendering uses the theme manager, and we don't want to call that code until we know it's finished initializing.
        _isLoading = false;
    }

    protected override void OnParametersSet()
    {
        EnabledOptions.Clear();
        EnabledOptions.Add(DashboardUIHelpers.PlaintextFormat);

        _options = [
            new SelectViewModel<string> { Id = DashboardUIHelpers.PlaintextFormat, Name = Loc[nameof(Resources.Dialogs.TextVisualizerDialogPlaintextFormat)] },
            new SelectViewModel<string> { Id = DashboardUIHelpers.JsonFormat, Name = Loc[nameof(Resources.Dialogs.TextVisualizerDialogJsonFormat)] },
            new SelectViewModel<string> { Id = DashboardUIHelpers.XmlFormat, Name = Loc[nameof(Resources.Dialogs.TextVisualizerDialogXmlFormat)] }
        ];

        // If a fixed format is specified, use it directly without auto-detection.
        if (Content.FixedFormat is not null)
        {
            TextVisualizerViewModel = new TextVisualizerViewModel(Content.Text, indentText: true, Content.FixedFormat);
        }
        else
        {
            TextVisualizerViewModel = new TextVisualizerViewModel(Content.Text, indentText: true);

            if (TextVisualizerViewModel.FormatKind == DashboardUIHelpers.JsonFormat)
            {
                EnabledOptions.Add(DashboardUIHelpers.JsonFormat);
            }
            else if (TextVisualizerViewModel.FormatKind == DashboardUIHelpers.XmlFormat)
            {
                EnabledOptions.Add(DashboardUIHelpers.XmlFormat);
            }
        }
    }

    private bool IsTextContentDisplayed
    {
        get
        {
            if (_isLoading)
            {
                return false;
            }

            return !Content.ContainsSecret || ShowSecretsWarning is false;
        }
    }

    private void OnFormatOptionChanged(MenuChangeEventArgs args) => ChangeFormat(args.Id, args.Value);

    public void ChangeFormat(string? newFormat, string? text)
    {
        _selectedFormat = text;
        TextVisualizerViewModel.UpdateFormat(newFormat ?? DashboardUIHelpers.PlaintextFormat);
    }

    public static async Task OpenDialogAsync(OpenTextVisualizerDialogOptions options)
    {
        var width = options.DialogService.IsDesktop ? "75vw" : "100vw";
        var parameters = new DialogParameters
        {
            Title = options.ValueDescription,
            Width = $"min(1000px, {width})",
            TrapFocus = true,
            Modal = true,
            PreventScroll = true,
        };

        await options.DialogService.ShowDialogAsync<TextVisualizerDialog>(
            new TextVisualizerDialogViewModel(options.Value, options.ValueDescription, options.ContainsSecret, options.DownloadFileName, options.FixedFormat), parameters);
    }

    private async Task DownloadAsync()
    {
        if (Content.DownloadFileName is not null)
        {
            await JS.DownloadFileAsync(Content.DownloadFileName, TextVisualizerViewModel.FormattedText);
        }
    }

    internal sealed record TextVisualizerDialogSettings(bool SecretsWarningAcknowledged);

    private async Task UnmaskContentAsync()
    {
        await LocalStorage.SetUnprotectedAsync(BrowserStorageKeys.TextVisualizerDialogSettings, new TextVisualizerDialogSettings(SecretsWarningAcknowledged: true));
        ShowSecretsWarning = false;
    }
}

