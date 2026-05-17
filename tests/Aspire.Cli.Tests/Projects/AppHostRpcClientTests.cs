// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Projects;
using Aspire.TypeSystem;

namespace Aspire.Cli.Tests.Projects;

public class AppHostRpcClientTests
{
    [Fact]
    public void NormalizeGeneratedFiles_TreatsNullResponseAsEmpty()
    {
        var files = AppHostRpcClient.NormalizeGeneratedFiles(null, "generateCode");

        Assert.Empty(files);
    }

    [Fact]
    public void NormalizeGeneratedFiles_RejectsMissingPath()
    {
        var files = new Dictionary<string, string>
        {
            [""] = "content"
        };

        var exception = Assert.Throws<InvalidOperationException>(() => AppHostRpcClient.NormalizeGeneratedFiles(files, "generateCode"));

        Assert.Contains("generateCode returned a generated file with no path", exception.Message);
    }

    [Fact]
    public void NormalizeGeneratedFiles_RejectsNullContent()
    {
        var files = new Dictionary<string, string>
        {
            ["AppHost.cs"] = null!
        };

        var exception = Assert.Throws<InvalidOperationException>(() => AppHostRpcClient.NormalizeGeneratedFiles(files, "scaffoldAppHost"));

        Assert.Contains("scaffoldAppHost returned null content for generated file 'AppHost.cs'", exception.Message);
    }

    [Fact]
    public void NormalizeRuntimeSpec_RejectsNullResponse()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => AppHostRpcClient.NormalizeRuntimeSpec(null, "getRuntimeSpec"));

        Assert.Contains("getRuntimeSpec returned null", exception.Message);
    }

    [Theory]
    [InlineData(nameof(RuntimeSpec.Language), "getRuntimeSpec.Language")]
    [InlineData(nameof(RuntimeSpec.DisplayName), "getRuntimeSpec.DisplayName")]
    [InlineData(nameof(RuntimeSpec.CodeGenLanguage), "getRuntimeSpec.CodeGenLanguage")]
    [InlineData(nameof(RuntimeSpec.DetectionPatterns), "getRuntimeSpec.DetectionPatterns")]
    [InlineData(nameof(RuntimeSpec.Execute), "getRuntimeSpec.Execute")]
    public void NormalizeRuntimeSpec_RejectsNullRequiredRuntimeMembers(string memberName, string expectedMemberName)
    {
        var spec = memberName switch
        {
            nameof(RuntimeSpec.Language) => CreateRuntimeSpec(language: null!),
            nameof(RuntimeSpec.DisplayName) => CreateRuntimeSpec(displayName: null!),
            nameof(RuntimeSpec.CodeGenLanguage) => CreateRuntimeSpec(codeGenLanguage: null!),
            nameof(RuntimeSpec.DetectionPatterns) => CreateRuntimeSpec(detectionPatterns: null!, useDetectionPatterns: true),
            nameof(RuntimeSpec.Execute) => CreateRuntimeSpec(execute: null!, useExecute: true),
            _ => throw new InvalidOperationException($"Unknown member '{memberName}'.")
        };

        var exception = Assert.Throws<InvalidOperationException>(() => AppHostRpcClient.NormalizeRuntimeSpec(spec, "getRuntimeSpec"));

        Assert.Contains(expectedMemberName, exception.Message);
    }

    [Fact]
    public void NormalizeRuntimeSpec_RejectsMalformedCommandSpec()
    {
        var spec = CreateRuntimeSpec(
            execute: new CommandSpec
            {
                Command = null!,
                Args = ["apphost.ts"]
            },
            useExecute: true);

        var exception = Assert.Throws<InvalidOperationException>(() => AppHostRpcClient.NormalizeRuntimeSpec(spec, "getRuntimeSpec"));

        Assert.Contains("getRuntimeSpec.Execute.Command", exception.Message);
    }

    [Fact]
    public void NormalizeRuntimeSpec_RejectsMalformedCommandArguments()
    {
        var spec = CreateRuntimeSpec(
            execute: new CommandSpec
            {
                Command = "node",
                Args = [null!]
            },
            useExecute: true);

        var exception = Assert.Throws<InvalidOperationException>(() => AppHostRpcClient.NormalizeRuntimeSpec(spec, "getRuntimeSpec"));

        Assert.Contains("getRuntimeSpec.Execute.Args[]", exception.Message);
    }

    [Fact]
    public void NormalizeRuntimeSpec_RejectsMalformedMigrationFiles()
    {
        var spec = CreateRuntimeSpec(
            migrationFiles: new Dictionary<string, string>
            {
                ["tsconfig.json"] = null!
            });

        var exception = Assert.Throws<InvalidOperationException>(() => AppHostRpcClient.NormalizeRuntimeSpec(spec, "getRuntimeSpec"));

        Assert.Contains("getRuntimeSpec.MigrationFiles['tsconfig.json']", exception.Message);
    }

    private static RuntimeSpec CreateRuntimeSpec(
        string? language = "typescript",
        string? displayName = "TypeScript",
        string? codeGenLanguage = "typescript",
        string[]? detectionPatterns = null,
        bool useDetectionPatterns = false,
        CommandSpec? execute = null,
        bool useExecute = false,
        Dictionary<string, string>? migrationFiles = null)
    {
        return new RuntimeSpec
        {
            Language = language!,
            DisplayName = displayName!,
            CodeGenLanguage = codeGenLanguage!,
            DetectionPatterns = useDetectionPatterns ? detectionPatterns! : ["apphost.ts"],
            Execute = useExecute ? execute! : new CommandSpec
            {
                Command = "node",
                Args = ["apphost.ts"]
            },
            MigrationFiles = migrationFiles
        };
    }
}
