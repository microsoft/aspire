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
            key => Assert.Equal("apphost.run.json", key));

        Assert.Contains("#[path = \".aspire/modules/mod.rs\"]", files["apphost.rs"], StringComparison.Ordinal);
        Assert.Contains("let builder = create_builder(None)?;", files["apphost.rs"], StringComparison.Ordinal);
        Assert.Contains("app.run(None)?;", files["apphost.rs"], StringComparison.Ordinal);
        Assert.Contains("[[bin]]", files["Cargo.toml"], StringComparison.Ordinal);
        Assert.Contains("name = \"apphost\"", files["Cargo.toml"], StringComparison.Ordinal);
        Assert.Contains("path = \"apphost.rs\"", files["Cargo.toml"], StringComparison.Ordinal);
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
        Assert.Equal("rust", runtimeSpec.ExtensionLaunchCapability);
        Assert.Equal("cargo", runtimeSpec.Execute.Command);
        Assert.Equal(["run", "--bin", "apphost", "--"], runtimeSpec.Execute.Args);
    }

    [Fact]
    public void GetRuntimeSpec_NamesTheScaffoldedBinarySoASecondBinTargetStaysUnambiguous()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var files = _languageSupport.Scaffold(new ScaffoldRequest
        {
            TargetPath = workspace.Path,
            ProjectName = "RustApp"
        });

        // Once the package declares a second [[bin]], a bare `cargo run` fails with "could not determine
        // which binary to run" and the app host never starts, so the launch command has to name the binary
        // the scaffolded manifest declares.
        var multiBinaryManifest = files["Cargo.toml"] + """


            [[bin]]
            name = "migrate"
            path = "migrate.rs"
            """;
        var runtimeSpec = _languageSupport.GetRuntimeSpec();

        Assert.Equal(["apphost", "migrate"], ReadBinTargetNames(multiBinaryManifest));
        Assert.Equal(["run", "--bin", "apphost", "--"], runtimeSpec.Execute.Args);
    }

    // Bin targets are declared in Cargo.toml as:
    //   [[bin]]
    //   name = "apphost"
    //   path = "apphost.rs"
    // Only the scaffolded shape is handled: a table header on its own line followed by unquoted keys.
    private static string[] ReadBinTargetNames(string manifest)
    {
        const string namePrefix = "name = \"";
        var names = new List<string>();
        var inBinTable = false;

        foreach (var line in manifest.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('['))
            {
                inBinTable = trimmed == "[[bin]]";
            }
            else if (inBinTable && trimmed.StartsWith(namePrefix, StringComparison.Ordinal))
            {
                names.Add(trimmed[namePrefix.Length..].TrimEnd('"'));
            }
        }

        return [.. names];
    }
}
