// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Acquisition;
using Aspire.Cli.Utils;

namespace Aspire.Cli.Tests.Acquisition;

public class UpgradeInstructionProviderTests
{
    private static readonly UpgradeInstructionProvider s_provider = new();

    // Routes that have a single canonical update command and don't depend on
    // processPath or identityChannel.
    [Theory]
    [InlineData("Winget", "winget upgrade Microsoft.Aspire")]
    [InlineData("Brew", "brew upgrade --cask aspire")]
    [InlineData("LocalHive", "Run ./localhive.sh (Linux/macOS) or .\\localhive.ps1 (Windows) in the local hive directory.")]
    public void GetUpdateCommand_StaticHintRoutes_ReturnExpectedCommand(string sourceName, string expected)
    {
        var source = Enum.Parse<InstallSource>(sourceName);
        var command = s_provider.GetUpdateCommand(source, processPath: null, identityChannel: "local");
        Assert.Equal(expected, command);
    }

    // Routes where there is intentionally no separate update command — script
    // gets the in-process flow.
    [Theory]
    [InlineData("Script")]
    public void GetUpdateCommand_NoHintRoutes_ReturnNull(string sourceName)
    {
        var source = Enum.Parse<InstallSource>(sourceName);
        Assert.Null(s_provider.GetUpdateCommand(source, processPath: null, identityChannel: "local"));
    }

    // PR-route hints substitute the PR number parsed from the CLI's identity
    // channel (CliExecutionContext.IdentityChannel == "pr-<N>" for PR builds).
    [Theory]
    [InlineData("pr-16817", "get-aspire-cli-pr.sh 16817    # or: get-aspire-cli-pr.ps1 -PRNumber 16817")]
    [InlineData("pr-1", "get-aspire-cli-pr.sh 1    # or: get-aspire-cli-pr.ps1 -PRNumber 1")]
    public void GetUpdateCommand_Pr_SubstitutesPrNumberFromIdentityChannel(string identityChannel, string expected)
    {
        Assert.Equal(expected, s_provider.GetUpdateCommand(InstallSource.Pr, processPath: null, identityChannel));
    }

    // Defensive: when a PR sidecar is read on a binary whose identity channel
    // is NOT a pr-<N> form (shouldn't happen in practice but locks in deterministic
    // output), emit the parameterised form so the user knows they must supply N.
    [Theory]
    [InlineData("stable")]
    [InlineData("daily")]
    [InlineData("local")]
    [InlineData("pr-")]   // pr- with no number
    [InlineData("")]
    public void GetUpdateCommand_Pr_WithNonPrIdentityChannel_FallsBackToParameterisedForm(string identityChannel)
    {
        var command = s_provider.GetUpdateCommand(InstallSource.Pr, processPath: null, identityChannel);
        Assert.Equal("get-aspire-cli-pr.sh <N>    # or: get-aspire-cli-pr.ps1 -PRNumber <N>", command);
    }

    [Fact]
    public void GetUpdateCommand_DotnetTool_Global_ReturnsGlobalUpdateCommand()
    {
        using var processPathScope = DotNetToolDetection.UseProcessPathForTesting(
            "/home/test/.dotnet/tools/.store/aspire.cli/9.4.0/aspire.cli.linux-x64/9.4.0/tools/net10.0/linux-x64/aspire");

        var command = s_provider.GetUpdateCommand(InstallSource.DotnetTool, processPath: null, identityChannel: "stable");

        Assert.Equal("dotnet tool update -g Aspire.Cli", command);
    }

    [Fact]
    public void GetUpdateCommand_DotnetTool_ToolPath_ReturnsPathAwareUpdateCommand()
    {
        // Standard --tool-path install layout: the binary lives under
        // <tool-path>/<rid>/<v>/tools/<tfm>/<rid>/aspire and its sibling .store
        // directory makes the path-shape detector recognize it. The tool path
        // is emitted unquoted when it contains no whitespace (see
        // DotNetToolDetection.QuoteCommandArgument). Build the path using
        // Path.DirectorySeparatorChar so the test passes on both Unix (where
        // it's `/`) and Windows (where DotNetToolDetection.NormalizeDirectorySeparators
        // produces `\` separators in the extracted toolPath).
        var s = Path.DirectorySeparatorChar;
        var toolPath = $"{s}opt{s}my-aspire";
        using var processPathScope = DotNetToolDetection.UseProcessPathForTesting(
            $"{toolPath}{s}.store{s}aspire.cli{s}9.4.0{s}aspire.cli.linux-x64{s}9.4.0{s}tools{s}net10.0{s}linux-x64{s}aspire");

        var command = s_provider.GetUpdateCommand(InstallSource.DotnetTool, processPath: null, identityChannel: "stable");

        Assert.Equal($"dotnet tool update --tool-path {toolPath} Aspire.Cli", command);
    }

    [Fact]
    public void GetUpdateCommand_DotnetTool_ToolPathWithSpaces_QuotesPath()
    {
        // Paths with whitespace get quoted by DotNetToolDetection.QuoteCommandArgument
        // so the resulting command remains a single argv element when copy-pasted
        // into a shell. Platform-native separators (see ToolPath test for context).
        var s = Path.DirectorySeparatorChar;
        var toolPath = $"{s}opt{s}My Aspire";
        using var processPathScope = DotNetToolDetection.UseProcessPathForTesting(
            $"{toolPath}{s}.store{s}aspire.cli{s}9.4.0{s}aspire.cli.linux-x64{s}9.4.0{s}tools{s}net10.0{s}linux-x64{s}aspire");

        var command = s_provider.GetUpdateCommand(InstallSource.DotnetTool, processPath: null, identityChannel: "stable");

        Assert.Equal($"dotnet tool update --tool-path \"{toolPath}\" Aspire.Cli", command);
    }

    [Fact]
    public void GetUpdateCommand_DotnetTool_PathShapeUnrecognized_FallsBackToGlobal()
    {
        // When the running process path doesn't match any known dotnet-tool
        // store layout (e.g., legacy installs without the canonical store
        // shape, or the test runner itself), the provider falls back to the
        // global form so the message is at least actionable.
        using var processPathScope = DotNetToolDetection.UseProcessPathForTesting("/random/path/aspire");

        var command = s_provider.GetUpdateCommand(InstallSource.DotnetTool, processPath: null, identityChannel: "stable");

        Assert.Equal("dotnet tool update -g Aspire.Cli", command);
    }

    [Fact]
    public void GetUpdateCommand_DotnetTool_UsesSuppliedProcessPath()
    {
        var s = Path.DirectorySeparatorChar;
        var toolPath = $"{s}opt{s}path-from-parameter";
        using var processPathScope = DotNetToolDetection.UseProcessPathForTesting("/random/path/aspire");

        var command = s_provider.GetUpdateCommand(
            InstallSource.DotnetTool,
            $"{toolPath}{s}.store{s}aspire.cli{s}9.4.0{s}aspire.cli.linux-x64{s}9.4.0{s}tools{s}net10.0{s}linux-x64{s}aspire",
            identityChannel: "stable");

        Assert.Equal($"dotnet tool update --tool-path {toolPath} Aspire.Cli", command);
    }

    [Fact]
    public void GetUpdateCommand_Unknown_ReturnsForceHint()
    {
        var command = s_provider.GetUpdateCommand(InstallSource.Unknown, processPath: null, identityChannel: "stable");

        Assert.NotNull(command);
        Assert.Contains("--force", command, StringComparison.Ordinal);
    }

    [Fact]
    public void GetUpdateCommand_LocalHive_ReturnsBothScriptNames()
    {
        var command = s_provider.GetUpdateCommand(InstallSource.LocalHive, processPath: null, identityChannel: "local");

        Assert.NotNull(command);
        Assert.Contains("localhive.sh", command, StringComparison.Ordinal);
        Assert.Contains("localhive.ps1", command, StringComparison.Ordinal);
    }
}
