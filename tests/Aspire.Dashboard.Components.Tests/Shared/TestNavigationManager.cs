// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.Components;

namespace Aspire.Dashboard.Components.Tests.Shared;

internal sealed class TestNavigationManager : NavigationManager
{
    public TestNavigationManager(string baseUri, string? uri = null)
    {
        Initialize(baseUri, uri ?? baseUri);
    }

    public NavigationOptions? LastNavigationOptions { get; private set; }

    protected override void NavigateToCore(string uri, NavigationOptions options)
    {
        LastNavigationOptions = options;
        NavigateToCore(uri, options.ForceLoad);
    }

    protected override void NavigateToCore(string uri, bool forceLoad)
    {
        Uri = ToAbsoluteUri(uri).AbsoluteUri;
        NotifyLocationChanged(isInterceptedLink: false);
    }
}
