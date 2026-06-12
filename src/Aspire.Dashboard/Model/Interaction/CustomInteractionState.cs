// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;

namespace Aspire.Dashboard.Model.Interaction;

/// <summary>
/// Tracks active menu buttons and page content updates from the AppHost interaction service.
/// </summary>
public sealed class CustomInteractionState
{
    private readonly object _lock = new();
    private ImmutableArray<MenuButtonState> _menuButtons = [];
    private ImmutableArray<IframeState> _iframes = [];

    public event Action? OnMenuButtonsChanged;
    public event Action<PageContentUpdate>? OnPageContentUpdated;
    public event Action? OnIframesChanged;

    public ImmutableArray<MenuButtonState> MenuButtons
    {
        get
        {
            lock (_lock)
            {
                return _menuButtons;
            }
        }
    }

    public ImmutableArray<IframeState> Iframes
    {
        get
        {
            lock (_lock)
            {
                return _iframes;
            }
        }
    }

    public void SetActiveIframe(string? route)
    {
        lock (_lock)
        {
            var changed = false;
            var builder = _iframes.ToBuilder();
            for (var i = 0; i < builder.Count; i++)
            {
                var shouldBeActive = string.Equals(builder[i].Route, route, StringComparison.OrdinalIgnoreCase);
                if (builder[i].IsActive != shouldBeActive)
                {
                    builder[i] = builder[i] with { IsActive = shouldBeActive };
                    changed = true;
                }
            }

            if (!changed)
            {
                return;
            }
            _iframes = builder.ToImmutable();
        }
        OnIframesChanged?.Invoke();
    }

    /// <summary>
    /// Registers or updates an iframe for the given route.
    /// </summary>
    public void SetIframe(string route, string iframeUrl)
    {
        lock (_lock)
        {
            var existing = _iframes.FirstOrDefault(f => string.Equals(f.Route, route, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                // URL already registered — no change needed.
                if (string.Equals(existing.IframeUrl, iframeUrl, StringComparison.Ordinal))
                {
                    return;
                }

                _iframes = _iframes.Replace(existing, existing with { IframeUrl = iframeUrl });
            }
            else
            {
                _iframes = _iframes.Add(new IframeState(route, iframeUrl));
            }
        }
        OnIframesChanged?.Invoke();
    }

    /// <summary>
    /// Removes an iframe when the page is unregistered or the user navigates away from a non-persistent iframe page.
    /// </summary>
    public void RemoveIframe(string route)
    {
        lock (_lock)
        {
            _iframes = _iframes.RemoveAll(f => string.Equals(f.Route, route, StringComparison.OrdinalIgnoreCase));
        }
        OnIframesChanged?.Invoke();
    }

    public void AddMenuButton(int interactionId, string iconName, string text, string url)
    {
        lock (_lock)
        {
            // Idempotent — don't add if already registered (e.g. on reconnection).
            if (_menuButtons.Any(b => b.InteractionId == interactionId))
            {
                return;
            }
            _menuButtons = _menuButtons.Add(new MenuButtonState(interactionId, iconName, text, url));
        }
        OnMenuButtonsChanged?.Invoke();
    }

    public void RemoveMenuButton(int interactionId)
    {
        lock (_lock)
        {
            _menuButtons = _menuButtons.RemoveAll(b => b.InteractionId == interactionId);
        }
        OnMenuButtonsChanged?.Invoke();
    }

    public void UpdatePageContent(int interactionId, string route, string sessionId, string content, string title, IReadOnlyList<string> styleIncludes, IReadOnlyList<string> scriptIncludes, bool enableHtml, string? iframeUrl, bool iframePersistent)
    {
        OnPageContentUpdated?.Invoke(new PageContentUpdate(interactionId, route, sessionId, content, title, styleIncludes, scriptIncludes, enableHtml, iframeUrl, iframePersistent));
    }
}

public sealed record MenuButtonState(int InteractionId, string IconName, string Text, string Url);

public sealed record PageContentUpdate(int InteractionId, string Route, string SessionId, string Content, string Title, IReadOnlyList<string> StyleIncludes, IReadOnlyList<string> ScriptIncludes, bool EnableHtml, string? IframeUrl, bool IframePersistent);

public sealed record IframeState(string Route, string IframeUrl, bool IsActive = false);
