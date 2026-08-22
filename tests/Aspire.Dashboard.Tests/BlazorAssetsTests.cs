// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;

namespace Aspire.Dashboard.Tests;

public class BlazorAssetsTests
{
    [Fact]
    public void BlazorScript_UsesTargetFrameworkStaticWebAsset()
    {
        var blazorScriptPath = Path.Combine(GetRepoRoot(), "src", "Aspire.Dashboard", "Components", "BlazorScript.razor");

        var blazorScript = File.ReadAllText(blazorScriptPath).Trim();

        Assert.Equal("""<script src="_framework/blazor.web.js"></script>""", blazorScript);
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