// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;

namespace Aspire.Dashboard.Tests;

public class BlazorAssetsTests
{
    [Theory]
    [InlineData("10")]
    [InlineData("11")]
    public void BlazorWebJs_DoesNotSendUnsupportedKeyboardEventProperties(string runtimeMajorVersion)
    {
        var blazorWebJs = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "wwwroot", "framework", $"blazor.web.{runtimeMajorVersion}.js"));

        Assert.Contains("keydown", blazorWebJs, StringComparison.Ordinal);
        Assert.False(
            blazorWebJs.Contains("isComposing", StringComparison.Ordinal),
            "The dashboard Blazor script must not emit KeyboardEvent.isComposing because the server event parser rejects the unknown property.");
    }
}