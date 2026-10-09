// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.Components;

namespace Aspire.Dashboard.Components.Tests.Shared;

internal sealed class TestNavigationManager : NavigationManager
{
    public TestNavigationManager(string baseUri)
    {
        Initialize(baseUri, baseUri);
    }

    protected override void NavigateToCore(string uri, bool forceLoad)
    {
        Uri = ToAbsoluteUri(uri).AbsoluteUri;
        NotifyLocationChanged(isInterceptedLink: false);
    }
}
