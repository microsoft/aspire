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
        var blazorWebJsPath = Path.Combine(GetRepoRoot(), "src", "Aspire.Dashboard", "wwwroot", "framework", $"blazor.web.{runtimeMajorVersion}.js");
        Assert.True(File.Exists(blazorWebJsPath), $"Expected generated Blazor asset at {blazorWebJsPath}");

        var blazorWebJs = File.ReadAllText(blazorWebJsPath);

        Assert.Contains("keydown", blazorWebJs, StringComparison.Ordinal);
        Assert.False(
            blazorWebJs.Contains("isComposing", StringComparison.Ordinal),
            "The dashboard Blazor script must not emit KeyboardEvent.isComposing because the server event parser rejects the unknown property.");
    }

    private static string GetRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Aspire.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory.FullName;
    }
}