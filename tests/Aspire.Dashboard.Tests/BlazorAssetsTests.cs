// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;

namespace Aspire.Dashboard.Tests;

public class BlazorAssetsTests
{
    [Fact]
    public void IMask_IsBundledAndLoadedBeforeBlazor()
    {
        var repoRoot = GetRepoRoot();
        var imaskPath = Path.Combine(repoRoot, "src", "Aspire.Dashboard", "wwwroot", "js", "imask-7.6.1.min.js");
        var appPath = Path.Combine(repoRoot, "src", "Aspire.Dashboard", "Components", "App.razor");

        Assert.True(File.Exists(imaskPath), $"Expected bundled IMask asset at {imaskPath}");

        var imask = File.ReadAllText(imaskPath);
        Assert.Contains("globalThis", imask, StringComparison.Ordinal);
        Assert.Contains(".IMask", imask, StringComparison.Ordinal);

        var app = File.ReadAllText(appPath);
        var imaskScriptIndex = app.IndexOf("""<script src="@Assets["js/imask-7.6.1.min.js"]"></script>""", StringComparison.Ordinal);
        var blazorScriptIndex = app.IndexOf("""<script src="@Assets["_framework/blazor.web.js"]"></script>""", StringComparison.Ordinal);
        Assert.True(imaskScriptIndex >= 0, "Expected App.razor to load the bundled IMask script.");
        Assert.True(blazorScriptIndex > imaskScriptIndex, "IMask must load before Blazor renders FluentNumberInput components.");
    }

    [Fact]
    public void BlazorScript_UsesAssetReference()
    {
        var appPath = Path.Combine(GetRepoRoot(), "src", "Aspire.Dashboard", "Components", "App.razor");

        var blazorScript = File.ReadLines(appPath)
            .Single(line => line.Contains("_framework/blazor.web.js", StringComparison.Ordinal)).Trim();

        Assert.Equal("""<script src="@Assets["_framework/blazor.web.js"]"></script>""", blazorScript);
    }

    [Fact]
    public void TerminalModules_UseAssetReferences()
    {
        var componentsPath = Path.Combine(GetRepoRoot(), "src", "Aspire.Dashboard", "Components");

        Assert.Contains(
            """Assets["Components/Layout/TerminalDock.razor.js"]""",
            File.ReadAllText(Path.Combine(componentsPath, "Layout", "TerminalDock.razor.cs")),
            StringComparison.Ordinal);
        Assert.Contains(
            """Assets["js/app-terminalwindow.js"]""",
            File.ReadAllText(Path.Combine(componentsPath, "Pages", "TerminalWindow.razor.cs")),
            StringComparison.Ordinal);
        Assert.Contains(
            """Assets["js/app-terminalwindow.js"]""",
            File.ReadAllText(Path.Combine(componentsPath, "Controls", "TerminalWindowButton.razor.cs")),
            StringComparison.Ordinal);
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