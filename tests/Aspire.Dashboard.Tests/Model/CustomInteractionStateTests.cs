// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model.Interaction;
using Xunit;

namespace Aspire.Dashboard.Tests.Model;

public class CustomInteractionStateTests
{
    [Fact]
    public void AddMenuButton_AddsToCollection()
    {
        var state = new CustomInteractionState();

        state.AddMenuButton(1, "Home", "Go Home", "/pages/home");

        var button = Assert.Single(state.MenuButtons);
        Assert.Equal(1, button.InteractionId);
        Assert.Equal("Home", button.IconName);
        Assert.Equal("Go Home", button.Text);
        Assert.Equal("/pages/home", button.Url);
    }

    [Fact]
    public void AddMenuButton_Duplicate_IsIdempotent()
    {
        var state = new CustomInteractionState();

        state.AddMenuButton(1, "Home", "Go Home", "/pages/home");
        state.AddMenuButton(1, "Home", "Go Home", "/pages/home");

        Assert.Single(state.MenuButtons);
    }

    [Fact]
    public void AddMenuButton_RaisesOnMenuButtonsChanged()
    {
        var state = new CustomInteractionState();
        var eventRaised = false;
        state.OnMenuButtonsChanged += () => eventRaised = true;

        state.AddMenuButton(1, "Home", "Go Home", "/pages/home");

        Assert.True(eventRaised);
    }

    [Fact]
    public void RemoveMenuButton_RemovesFromCollection()
    {
        var state = new CustomInteractionState();
        state.AddMenuButton(1, "Home", "Go Home", "/pages/home");

        state.RemoveMenuButton(1);

        Assert.Empty(state.MenuButtons);
    }

    [Fact]
    public void RemoveMenuButton_RaisesOnMenuButtonsChanged()
    {
        var state = new CustomInteractionState();
        state.AddMenuButton(1, "Home", "Go Home", "/pages/home");

        var eventRaised = false;
        state.OnMenuButtonsChanged += () => eventRaised = true;

        state.RemoveMenuButton(1);

        Assert.True(eventRaised);
    }

    [Fact]
    public void UpdatePageContent_RaisesOnPageContentUpdated()
    {
        var state = new CustomInteractionState();
        PageContentUpdate? receivedUpdate = null;
        state.OnPageContentUpdated += update => receivedUpdate = update;

        state.UpdatePageContent(1, "my-page", "session-1", "# Hello", "My Page", ["site.css"], ["site.js"], enableHtml: true, iframeUrl: "https://example.com", iframePersistent: true);

        Assert.NotNull(receivedUpdate);
        Assert.Equal(1, receivedUpdate.InteractionId);
        Assert.Equal("my-page", receivedUpdate.Route);
        Assert.Equal("session-1", receivedUpdate.SessionId);
        Assert.Equal("# Hello", receivedUpdate.Content);
        Assert.Equal("My Page", receivedUpdate.Title);
        Assert.Equal(["site.css"], receivedUpdate.StyleIncludes);
        Assert.Equal(["site.js"], receivedUpdate.ScriptIncludes);
        Assert.True(receivedUpdate.EnableHtml);
        Assert.Equal("https://example.com", receivedUpdate.IframeUrl);
    }

    [Fact]
    public void MultipleMenuButtons_TrackedIndependently()
    {
        var state = new CustomInteractionState();

        state.AddMenuButton(1, "Home", "Home", "/home");
        state.AddMenuButton(2, "Settings", "Settings", "/settings");

        Assert.Equal(2, state.MenuButtons.Length);

        state.RemoveMenuButton(1);

        var remaining = Assert.Single(state.MenuButtons);
        Assert.Equal(2, remaining.InteractionId);
    }

}
