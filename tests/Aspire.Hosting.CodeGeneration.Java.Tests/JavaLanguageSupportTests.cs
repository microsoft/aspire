// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

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

    private sealed class LegacyCommandSpec
    {
    }

    private static void AssertCompileUpToDateCheck(CommandSpec compile)
    {
        var check = Assert.IsType<CommandUpToDateCheck>(compile.UpToDateCheck);

        Assert.Equal(["{appHostFile}", "./**", ".aspire/modules/**", "src/main/java/**"], check.Inputs);
        Assert.Equal([".java"], check.FileExtensions!);
        Assert.Equal(Path.Combine(".java-build", ".aspire-compile-stamp"), check.StampFile);
    }
}
