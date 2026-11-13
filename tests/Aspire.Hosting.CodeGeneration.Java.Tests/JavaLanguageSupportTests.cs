// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;

using Aspire.TypeSystem;

namespace Aspire.Hosting.CodeGeneration.Java.Tests;

public class JavaLanguageSupportTests
{
    [Fact]
    public void GetRuntimeSpec_SetsCompileUpToDateCheckWhenSupported()
    {
        var runtimeSpec = new JavaLanguageSupport().GetRuntimeSpec();
        var compile = Assert.Single(runtimeSpec.PreExecute!);

        AssertCompileUpToDateCheck(compile);
    }

    [Fact]
    public void JavaStarter_UsesRuntimeSpecJavaRelease()
    {
        var runtimeSpec = new JavaLanguageSupport().GetRuntimeSpec();
        var compile = Assert.Single(runtimeSpec.PreExecute!);
        var releaseArgumentIndex = Array.IndexOf(compile.Args, "--release");

        Assert.InRange(releaseArgumentIndex, 0, compile.Args.Length - 2);
        var runtimeRelease = compile.Args[releaseArgumentIndex + 1];

        var buildFilePath = Path.Combine(
            GetRepoRoot(),
            "src",
            "Aspire.Cli",
            "Templating",
            "Templates",
            "java-starter",
            "api",
            "build.gradle");
        var buildFile = File.ReadAllText(buildFilePath);
        var match = Regex.Match(
            buildFile,
            @"languageVersion\s*=\s*JavaLanguageVersion\.of\((?<release>\d+)\)");

        Assert.True(match.Success, $"Could not find the Java toolchain language version in '{buildFilePath}'.");
        Assert.Equal(runtimeRelease, match.Groups["release"].Value);
    }

    [Fact]
    public void SetCompileUpToDateCheckIfSupported_PopulatesCompileUpToDateCheckOnCommandSpec()
    {
        var compile = new CommandSpec
        {
            Command = "javac",
            Args = ["--release", "25", "-d", ".java-build", "@.aspire/modules/sources.txt", "{appHostFile}"]
        };

        JavaLanguageSupport.SetCompileUpToDateCheckIfSupported(compile, ".java-build");

        AssertCompileUpToDateCheck(compile);
    }

    [Fact]
    public void SetCompileUpToDateCheckIfSupported_IgnoresLegacyCommandSpec()
    {
        var exception = Record.Exception(() =>
            JavaLanguageSupport.SetCompileUpToDateCheckIfSupported(
                new LegacyCommandSpec(),
                ".java-build"));

        Assert.Null(exception);
    }

    [Fact]
    public void SetCompileUpToDateCheckIfSupported_IgnoresLegacyUpToDateCheckWithoutOutputs()
    {
        var command = new LegacyCommandSpecWithUpToDateCheck();

        var exception = Record.Exception(() =>
            JavaLanguageSupport.SetCompileUpToDateCheckIfSupported(
                command,
                ".java-build"));

        Assert.Null(exception);
        Assert.Null(command.UpToDateCheck);
    }

    private sealed class LegacyCommandSpec
    {
    }

    private sealed class LegacyCommandSpecWithUpToDateCheck
    {
        public LegacyCommandUpToDateCheck? UpToDateCheck { get; set; }
    }

    private sealed class LegacyCommandUpToDateCheck
    {
        public string[]? Inputs { get; set; }
        public string[]? FileExtensions { get; set; }
        public string? StampFile { get; set; }
    }

    private static string GetRepoRoot()
        => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static void AssertCompileUpToDateCheck(CommandSpec compile)
    {
        var check = Assert.IsType<CommandUpToDateCheck>(compile.UpToDateCheck);

        Assert.Equal(["{appHostFile}", "./**", ".aspire/modules/**", "src/main/java/**"], check.Inputs);
        Assert.Equal([Path.Combine(".java-build", "AppHost.class")], check.Outputs!);
        Assert.Equal([".java"], check.FileExtensions!);
        Assert.Equal(Path.Combine(".java-build", ".aspire-compile-stamp"), check.StampFile);
    }
}
