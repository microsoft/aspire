// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.Interaction;
using Aspire.Dashboard.Model.Markdown;
using Aspire.Dashboard.Resources;
using Aspire.Dashboard.Utils;
using Aspire.DashboardService.Proto.V1;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Localization;
using Microsoft.JSInterop;
using DashboardResources = Aspire.Dashboard.Resources.Resources;

namespace Aspire.Dashboard.Components.Pages;

public partial class CustomPage : ComponentBase, IAsyncDisposable
{
    private const string CustomPageContainerId = "custom-page-container";

    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private MarkdownProcessor _markdownProcessor = default!;
    private string? _content;
    private string? _iframeUrl;
    private bool _iframePersistent;
    private string _pageTitle = "Page";
    private bool _pageNotFound;
    private bool _interactionStarted;
    private bool _pageAssetsChanged;
    private string? _currentRoute;
    private StartPageInteractionResult? _pageInteraction;
    private PageContentUpdate? _pendingPageContentUpdate;
    private IReadOnlyList<string> _styleIncludes = [];
    private IReadOnlyList<string> _scriptIncludes = [];
    private bool _enableHtml;
    private IJSObjectReference? _jsModule;
    private List<string>? _activeCssHrefs;
    private List<IJSObjectReference>? _pageScriptModules;
    private DotNetObjectReference<CustomPageInterop>? _interopReference;

    [Parameter]
    public string? Route { get; set; }

    [Inject]
    public required IDashboardClient DashboardClient { get; init; }

    [Inject]
    public required CustomInteractionState CustomInteractionState { get; init; }

    [Inject]
    public required NavigationManager NavigationManager { get; init; }

    [Inject]
    public required IconResolver IconResolver { get; init; }

    [Inject]
    public required IStringLocalizer<ControlsStrings> ControlsStringsLoc { get; init; }

    [Inject]
    public required IStringLocalizer<DashboardResources> ResourcesLoc { get; init; }

    [Inject]
    public required IJSRuntime JS { get; init; }

    [Inject]
    public required ILogger<CustomPage> Logger { get; init; }

    protected override void OnInitialized()
    {
        _markdownProcessor = InteractionMarkdownHelper.CreateProcessor(ControlsStringsLoc, [new ButtonExtension(IconResolver)]);
        _interopReference = DotNetObjectReference.Create(new CustomPageInterop(this));
        CustomInteractionState.OnPageContentUpdated += OnPageContentUpdated;
    }

    protected override async Task OnParametersSetAsync()
    {
        if (Route is null)
        {
            _pageNotFound = true;
            return;
        }

        // When the route changes (navigating between custom pages), complete the old page interaction
        // and reset state so the new page gets a fresh interaction.
        if (_interactionStarted && !string.Equals(_currentRoute, Route, StringComparison.OrdinalIgnoreCase))
        {
            RemoveIframeIfNotPersistent();
            CustomInteractionState.SetActiveIframe(null);
            await CompletePageInteractionAsync().ConfigureAwait(false);
            _interactionStarted = false;
            _content = null;
            _iframeUrl = null;
            _pageInteraction = null;
            _pendingPageContentUpdate = null;
            _styleIncludes = [];
            _scriptIncludes = [];
            _enableHtml = false;
            _pageTitle = "Page";
            _pageAssetsChanged = true;
        }

        _currentRoute = Route;

        if (!_interactionStarted)
        {
            var queryParameters = new Dictionary<string, string>(StringComparer.Ordinal);
            var uri = new Uri(NavigationManager.Uri);
            foreach (var (key, values) in QueryHelpers.ParseQuery(uri.Query))
            {
                queryParameters[key] = values.ToString();
            }

            var pageInteraction = await DashboardClient.StartPageInteractionAsync(Route, _sessionId, queryParameters, CancellationToken.None).ConfigureAwait(false);
            if (pageInteraction is null)
            {
                _pageNotFound = true;
                _content = null;
                _iframeUrl = null;
                _pageInteraction = null;
                _pendingPageContentUpdate = null;
                _styleIncludes = [];
                _scriptIncludes = [];
                _enableHtml = false;
                _pageAssetsChanged = true;
                return;
            }

            _pageInteraction = pageInteraction;
            _interactionStarted = true;
            _pageNotFound = false;

            if (_pendingPageContentUpdate is { } pendingUpdate && TryApplyPageContentUpdate(pendingUpdate))
            {
                _pendingPageContentUpdate = null;
            }
            else
            {
                _pendingPageContentUpdate = null;
            }
        }
    }

    private void OnPageContentUpdated(PageContentUpdate update)
    {
        if (_pageInteraction is null)
        {
            if (string.Equals(update.SessionId, _sessionId, StringComparison.Ordinal))
            {
                _pendingPageContentUpdate = update;
            }

            return;
        }

        if (TryApplyPageContentUpdate(update))
        {
            InvokeAsync(StateHasChanged);
        }
    }

    private bool TryApplyPageContentUpdate(PageContentUpdate update)
    {
        // Only process updates for our interaction and session.
        if (_pageInteraction is not null &&
            update.InteractionId == _pageInteraction.InteractionId &&
            string.Equals(update.SessionId, _sessionId, StringComparison.Ordinal))
        {
            var assetsChanged = !_styleIncludes.SequenceEqual(update.StyleIncludes) ||
                !_scriptIncludes.SequenceEqual(update.ScriptIncludes);

            _pageTitle = update.Title;
            _styleIncludes = update.StyleIncludes;
            _scriptIncludes = update.ScriptIncludes;

            if (_enableHtml != update.EnableHtml)
            {
                _enableHtml = update.EnableHtml;
                _markdownProcessor = InteractionMarkdownHelper.CreateProcessor(
                    ControlsStringsLoc,
                    [new ButtonExtension(IconResolver)],
                    enableHtml: _enableHtml);
            }

            _pageAssetsChanged |= assetsChanged;
            _content = update.Content;
            _iframeUrl = string.IsNullOrEmpty(update.IframeUrl) ? null : update.IframeUrl;
            _iframePersistent = update.IframePersistent;

            // Display iframe via the IframeContainer in the layout.
            if (_iframeUrl is not null && Route is not null)
            {
                CustomInteractionState.SetIframe(Route, _iframeUrl);
                CustomInteractionState.SetActiveIframe(Route);
            }
            else
            {
                CustomInteractionState.SetActiveIframe(null);
            }

            return true;
        }

        return false;
    }

    public async ValueTask DisposeAsync()
    {
        RemoveIframeIfNotPersistent();
        CustomInteractionState.SetActiveIframe(null);
        CustomInteractionState.OnPageContentUpdated -= OnPageContentUpdated;
        _interopReference?.Dispose();

        await RemovePageAssetsAsync();
        await JSInteropHelpers.SafeDisposeAsync(_jsModule);

        if (_interactionStarted)
        {
            await CompletePageInteractionAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Removes the iframe from the container if it is not persistent.
    /// Non-persistent iframes are only displayed while the page is active.
    /// </summary>
    private void RemoveIframeIfNotPersistent()
    {
        if (!_iframePersistent && _iframeUrl is not null && Route is not null)
        {
            CustomInteractionState.RemoveIframe(Route);
        }
    }

    private async Task CompletePageInteractionAsync()
    {
        if (_pageInteraction is null)
        {
            return;
        }

        var request = new WatchInteractionsRequestUpdate
        {
            InteractionId = _pageInteraction.InteractionId,
            Complete = new InteractionComplete()
        };

        try
        {
            await DashboardClient.SendInteractionRequestAsync(request, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best effort — the host may already be gone.
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _jsModule = await JS.InvokeAsync<IJSObjectReference>("import", "./Components/Pages/CustomPage.razor.js");
            await _jsModule.InvokeVoidAsync("attachButtonClickEvent", CustomPageContainerId, _interopReference);
            await AddPageAssetsAsync();
        }
        else if (_pageAssetsChanged)
        {
            _pageAssetsChanged = false;
            await RemovePageAssetsAsync();
            await AddPageAssetsAsync();
        }
    }

    /// <summary>
    /// Adds page-specific CSS links and loads script modules for the current page interaction.
    /// </summary>
    private async Task AddPageAssetsAsync()
    {
        if (_jsModule is null || _pageInteraction is null)
        {
            return;
        }

        if (_styleIncludes is { Count: > 0 } styleIncludes)
        {
            _activeCssHrefs = new List<string>(styleIncludes.Count);
            foreach (var styleInclude in styleIncludes)
            {
                var href = $"/assets/{styleInclude}";
                await _jsModule.InvokeVoidAsync("addStylesheetLink", href);
                _activeCssHrefs.Add(href);
            }
        }

        if (_scriptIncludes is { Count: > 0 } scriptIncludes)
        {
            _pageScriptModules = new List<IJSObjectReference>(scriptIncludes.Count);
            foreach (var scriptInclude in scriptIncludes)
            {
                var module = await JS.InvokeAsync<IJSObjectReference>("import", $"/assets/{scriptInclude}");
                _pageScriptModules.Add(module);
                await _jsModule.InvokeVoidAsync("invokeOptionalExport", module, "initialize");
            }
        }
    }

    /// <summary>
    /// Removes previously added CSS links and disposes script modules.
    /// </summary>
    private async Task RemovePageAssetsAsync()
    {
        if (_pageScriptModules is not null)
        {
            var jsModule = _jsModule;
            foreach (var module in _pageScriptModules)
            {
                if (jsModule is not null)
                {
                    try
                    {
                        await jsModule.InvokeVoidAsync("invokeOptionalExport", module, "dispose");
                    }
                    catch (Exception)
                    {
                        // Best effort — page scripts don't have to export a dispose function.
                    }
                }

                await JSInteropHelpers.SafeDisposeAsync(module);
            }
            _pageScriptModules = null;
        }

        if (_jsModule is not null && _activeCssHrefs is not null)
        {
            foreach (var href in _activeCssHrefs)
            {
                try
                {
                    await _jsModule.InvokeVoidAsync("removeStylesheetLink", href);
                }
                catch (Exception)
                {
                    // Best effort — JS runtime may be disconnected.
                }
            }
            _activeCssHrefs = null;
        }
    }

    /// <summary>
    /// Handles button clicks from markdown-rendered buttons in the custom page.
    /// </summary>
    private sealed class CustomPageInterop
    {
        private readonly CustomPage _page;

        public CustomPageInterop(CustomPage page)
        {
            _page = page;
        }

        [JSInvokable]
        public async Task OnButtonClick(IDictionary<string, string> values)
        {
            values.TryGetValue("action", out var actionName);
            values.TryGetValue("arguments", out var argumentsValue);

            if (string.IsNullOrEmpty(actionName))
            {
                _page.Logger.LogDebug("Button click missing required action value.");
                return;
            }

            if (_page._pageInteraction is not { } pageInteraction)
            {
                _page.Logger.LogDebug("Button click for action {ActionName} arrived before the page interaction started.", actionName);
                return;
            }

            var arguments = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!string.IsNullOrEmpty(argumentsValue))
            {
                try
                {
                    var parsed = QueryHelpers.ParseQuery(argumentsValue);
                    foreach (var (key, value) in parsed)
                    {
                        arguments[key] = value.ToString();
                    }
                }
                catch (Exception ex)
                {
                    _page.Logger.LogDebug(ex, "Failed to parse button arguments as query string: {Arguments}", argumentsValue);
                    return;
                }
            }

            var request = new WatchInteractionsRequestUpdate
            {
                InteractionId = pageInteraction.InteractionId,
                PageAction = new InteractionPageAction { ActionName = actionName }
            };

            request.PageAction.Arguments.Add(arguments);
            await _page.DashboardClient.SendInteractionRequestAsync(request, CancellationToken.None);
        }
    }
}
