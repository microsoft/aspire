// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TypeSystem;

namespace Aspire.Hosting.CodeGeneration.Rust.Tests;

public class RustLanguageSupportTests(ITestOutputHelper outputHelper)
{
    private readonly RustLanguageSupport _languageSupport = new();

    [Fact]
    public void Scaffold_CreatesRustAppHostFilesOnly()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var files = _languageSupport.Scaffold(new ScaffoldRequest
        {
            TargetPath = workspace.Path,
            ProjectName = "RustApp"
        });

        Assert.Collection(
            files.Keys.Order(StringComparer.Ordinal),
            key => Assert.Equal("Cargo.toml", key),
            key => Assert.Equal("apphost.rs", key),
            key => Assert.Equal("apphost.run.json", key),
            key => Assert.Equal("src/main.rs", key));
    }

    [Fact]
    public void Detect_ReturnsRustAppHostWhenMarkerAndCargoExist()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        File.WriteAllText(Path.Combine(workspace.Path, "apphost.rs"), "// marker");
        File.WriteAllText(Path.Combine(workspace.Path, "Cargo.toml"), "[package]");

        var result = _languageSupport.Detect(workspace.Path);

        Assert.True(result.IsValid);
        Assert.Equal("rust", result.Language);
        Assert.Equal("apphost.rs", result.AppHostFile);
    }

    [Fact]
    public void Detect_DoesNotTreatTypeScriptAppHostAsRust()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        File.WriteAllText(Path.Combine(workspace.Path, "apphost.ts"), "// typescript");
        File.WriteAllText(Path.Combine(workspace.Path, "Cargo.toml"), "[package]");

        var result = _languageSupport.Detect(workspace.Path);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Detect_RequiresCargoManifest()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        File.WriteAllText(Path.Combine(workspace.Path, "apphost.rs"), "// marker");

        var result = _languageSupport.Detect(workspace.Path);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void GetRuntimeSpec_UsesCargoRun()
    {
        var runtimeSpec = _languageSupport.GetRuntimeSpec();

        Assert.Equal("rust", runtimeSpec.Language);
        Assert.Equal("Rust", runtimeSpec.DisplayName);
        Assert.Equal("Rust", runtimeSpec.CodeGenLanguage);
        Assert.Equal(["apphost.rs"], runtimeSpec.DetectionPatterns);
        Assert.Equal("cargo", runtimeSpec.Execute.Command);
        Assert.Equal(["run"], runtimeSpec.Execute.Args);
    }
}
