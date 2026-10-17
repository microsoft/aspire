// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

public sealed class InvokeNativeArchiveBuildTests : IDisposable
{
    private readonly TemporaryWorkspace _workspace;
    private readonly string _scriptPath;
    private readonly ITestOutputHelper _output;

    public InvokeNativeArchiveBuildTests(ITestOutputHelper output)
    {
        _output = output;
        _workspace = TemporaryWorkspace.Create(output);
        _scriptPath = Path.Combine(RepoRoot.Path, "eng", "scripts", "invoke-native-archive-build.ps1");
    }

    public void Dispose() => _workspace.Dispose();

    [Theory]
    [InlineData("", new string[] { })]
    [InlineData(" \t\r\n", new string[] { })]
    [InlineData("/p:Version=1 /p:AssemblyVersion=2", new[] { "/p:Version=1", "/p:AssemblyVersion=2" })]
    [InlineData("$(Get-Date);&|*?", new[] { "$(Get-Date);&|*?" })]
    [RequiresTools(["pwsh"])]
    public async Task PassesVersionOverridesAsLiteralArguments(
        string versionOverrideArg,
        string[] expectedOverrideArgs)
    {
        var invocationPath = Path.Combine(_workspace.Path, "invoke-build.ps1");
        var stubPath = Path.Combine(_workspace.Path, "capture-arguments.ps1");
        var capturedArgumentsPath = Path.Combine(_workspace.Path, "arguments.json");
        File.WriteAllText(
            invocationPath,
            "$argumentList = @('base-one', 'base-two')\n" +
            "& $env:NATIVE_ARCHIVE_BUILD_SCRIPT -CommandPath $env:ARGUMENT_CAPTURE_STUB -ArgumentList $argumentList -VersionOverrideArg $env:VERSION_OVERRIDE_ARG");
        File.WriteAllText(
            stubPath,
            "ConvertTo-Json -InputObject $args -Compress | Set-Content -NoNewline -LiteralPath $env:CAPTURED_ARGUMENTS_PATH");

        using var cmd = new PowerShellCommand(invocationPath, _output)
            .WithTimeout(TimeSpan.FromMinutes(1))
            .WithEnvironmentVariable("VERSION_OVERRIDE_ARG", versionOverrideArg)
            .WithEnvironmentVariable("NATIVE_ARCHIVE_BUILD_SCRIPT", _scriptPath)
            .WithEnvironmentVariable("ARGUMENT_CAPTURE_STUB", stubPath)
            .WithEnvironmentVariable("CAPTURED_ARGUMENTS_PATH", capturedArgumentsPath);

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();

        var actualArguments = JsonSerializer.Deserialize<string[]>(File.ReadAllText(capturedArgumentsPath));
        Assert.NotNull(actualArguments);
        Assert.Equal(["base-one", "base-two", .. expectedOverrideArgs], actualArguments);
    }
}
