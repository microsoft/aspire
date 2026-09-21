// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model.Interaction;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;

namespace Aspire.Dashboard.Components.Dialogs;

public partial class InteractionsTerminalDialog
{
    private InteractionsTerminalDialogViewModel? _content;

    [Parameter]
    public InteractionsTerminalDialogViewModel Content { get; set; } = default!;

    [CascadingParameter]
    public IDialogInstance Dialog { get; set; } = default!;

    // Keep this relative to the dashboard base URI, not the current page. Terminal IDs are opaque and can contain
    // query/path delimiters (for example "terminal #1/?%+"), so encode the entire ID as a single query value.
    private string EndpointPathAndQuery => $"api/apphost-terminal?terminalId={Uri.EscapeDataString(Content.TerminalId)}";

    protected override void OnParametersSet()
    {
        if (_content != Content)
        {
            _content?.OnInteractionUpdated = null;
            _content = Content;
            _content.OnInteractionUpdated = () => InvokeAsync(StateHasChanged);
        }
    }

    private Task CancelAsync() => Dialog.CloseAsync(DialogResult.Ok("cancel"));

    public void Dispose()
    {
        _content?.OnInteractionUpdated = null;
    }
}
