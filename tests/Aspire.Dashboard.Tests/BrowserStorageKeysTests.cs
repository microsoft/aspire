// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Utils;
using Xunit;

namespace Aspire.Dashboard.Tests;

public class BrowserStorageKeysTests
{
    [Theory]
    [InlineData("my-app", "my app")]
    [InlineData("my_app", "my app")]
    [InlineData("日本語", "한국어")]
    public void CollapsedResourceNamesKey_SanitizationDoesNotCauseCollisions(string firstApplicationName, string secondApplicationName)
    {
        Assert.NotEqual(
            BrowserStorageKeys.CollapsedResourceNamesKey(firstApplicationName),
            BrowserStorageKeys.CollapsedResourceNamesKey(secondApplicationName));
    }
}
