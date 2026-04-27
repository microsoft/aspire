// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Utils;
using Aspire.Cli.Projects;

namespace Aspire.Cli.Tests.Utils;

public class MissingJavaScriptToolWarningTests(ITestOutputHelper outputHelper)
{
    private static readonly LanguageInfo s_typeScriptLanguage = new(
        LanguageId: new LanguageId(KnownLanguageId.TypeScript),
        DisplayName: "TypeScript (Node.js)",
        PackageName: "Aspire.Hosting.CodeGeneration.TypeScript",
        DetectionPatterns: ["apphost.ts"],
        CodeGenerator: "TypeScript",
        AppHostFileName: "apphost.ts");

    [Theory]
    [InlineData("npm is not installed or not found in PATH. Please install Node.js and try again.")]
    [InlineData("npx is not installed or not found in PATH. Please install Node.js and try again.")]
    [InlineData("bun is not installed or not found in PATH. Please install Bun and try again.")]
    [InlineData("yarn is not installed or not found in PATH. Please install Yarn and try again.")]
    [InlineData("pnpm is not installed or not found in PATH. Please install pnpm and try again.")]
    public void IsMatch_WhenJavaScriptToolIsMissing_ReturnsTrue(string message)
    {
        var lines = new[]
        {
            (OutputLineStream.StdErr, message)
        };

        Assert.True(MissingJavaScriptToolWarning.IsMatch(lines));
    }

    [Fact]
    public void IsMatch_WhenOutputIsUnrelated_ReturnsFalse()
    {
        var lines = new[]
        {
            (OutputLineStream.StdErr, "npm ERR! code E401"),
            (OutputLineStream.StdOut, "Installing packages...")
        };

        Assert.False(MissingJavaScriptToolWarning.IsMatch(lines));
    }

    [Fact]
    public void GetMessage_WhenTypeScriptProjectUsesBun_ReturnsToolchainSpecificInstallGuidance()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        File.WriteAllText(Path.Combine(workspace.WorkspaceRoot.FullName, "package.json"), "{ \"packageManager\": \"bun@1.2.0\" }");

        var message = MissingJavaScriptToolWarning.GetMessage(workspace.WorkspaceRoot, s_typeScriptLanguage);

        Assert.Contains("'bun install'", message, StringComparison.Ordinal);
        Assert.Contains("install Bun", message, StringComparison.Ordinal);
    }
}
