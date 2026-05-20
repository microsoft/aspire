// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;

namespace Aspire.Acquisition.Tests.Scripts;

/// <summary>
/// Source-level contract tests for localhive installer scripts.
/// </summary>
public class LocalHiveScriptFunctionTests
{
    [Theory]
    [InlineData(nameof(ScriptPaths.LocalHiveShell))]
    [InlineData(nameof(ScriptPaths.LocalHivePowerShell))]
    public void Source_DoesNotReferencePersistentShellProfileFiles(string scriptPathName)
    {
        var source = File.ReadAllText(GetRepoPath(GetScriptPath(scriptPathName)));

        Assert.DoesNotContain(".bashrc", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".zshrc", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".profile", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".bash_profile", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(nameof(ScriptPaths.LocalHivePowerShell))]
    public void PowerShellSource_DoesNotWriteUserPathEnvironment(string scriptPathName)
    {
        var source = File.ReadAllText(GetRepoPath(GetScriptPath(scriptPathName)));
        var userEnvironmentWrite = new System.Text.RegularExpressions.Regex(
            @"\[Environment\]::SetEnvironmentVariable\([^)]*(['""`]User['""`]|EnvironmentVariableTarget\]::User)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);

        Assert.False(
            userEnvironmentWrite.IsMatch(source),
            $"{GetScriptPath(scriptPathName)} must not call [Environment]::SetEnvironmentVariable(..., 'User') or EnvironmentVariableTarget.User.");
    }

    [Fact]
    public void ShellSource_PrintsDirectBinaryPathActivationHint()
    {
        var source = File.ReadAllText(GetRepoPath(ScriptPaths.LocalHiveShell));

        Assert.Contains("Run Aspire directly with: $CLI_BIN_DIR/$CLI_EXE_NAME", source, StringComparison.Ordinal);
        Assert.Contains("For this shell only, run: export PATH=", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Add this to your shell profile", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Add the following to ~/.bashrc", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Add to your shell profile", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PowerShellSource_PrintsDirectBinaryPathActivationHint()
    {
        var source = File.ReadAllText(GetRepoPath(ScriptPaths.LocalHivePowerShell));

        Assert.Contains("Run Aspire directly with: $installedCliPath", source, StringComparison.Ordinal);
        Assert.Contains("to PATH for this PowerShell session", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Add this to your shell profile", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Add the following to ~/.bashrc", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Add to your shell profile", source, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(nameof(ScriptPaths.LocalHiveShell))]
    [InlineData(nameof(ScriptPaths.LocalHivePowerShell))]
    public void Source_DoesNotWriteGlobalChannel(string scriptPathName)
    {
        var source = File.ReadAllText(GetRepoPath(GetScriptPath(scriptPathName)));

        Assert.DoesNotContain("config set channel", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("aspire config set", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PowerShellSource_WindowsArchivePathDoesNotUseCompressArchive()
    {
        // Compress-Archive enumerates inputs via the PowerShell provider, which on
        // non-Windows hosts hides dotfiles from `<dir>/*` wildcard expansion. The
        // portable layout includes bin/.aspire-install.json — the localhive route
        // sidecar — and silently dropping it from a win-* zip built on Linux/macOS
        // would produce sidecar-less installs on the target. Use
        // [System.IO.Compression.ZipFile]::CreateFromDirectory instead.
        var source = File.ReadAllText(GetRepoPath(ScriptPaths.LocalHivePowerShell));

        // Strip PowerShell line comments so the contract check matches actual
        // cmdlet invocations, not mentions of the cmdlet in explanatory comments.
        var sourceWithoutComments = string.Join('\n', source.Split('\n').Select(line =>
        {
            var hashIdx = line.IndexOf('#');
            return hashIdx >= 0 ? line[..hashIdx] : line;
        }));

        Assert.DoesNotContain("Compress-Archive", sourceWithoutComments, StringComparison.Ordinal);
        Assert.Contains("[System.IO.Compression.ZipFile]::CreateFromDirectory", source, StringComparison.Ordinal);
    }

    private static string GetScriptPath(string scriptPathName) => scriptPathName switch
    {
        nameof(ScriptPaths.LocalHiveShell) => ScriptPaths.LocalHiveShell,
        nameof(ScriptPaths.LocalHivePowerShell) => ScriptPaths.LocalHivePowerShell,
        _ => throw new ArgumentOutOfRangeException(nameof(scriptPathName), scriptPathName, null)
    };

    private static string GetRepoPath(string relativePath)
    {
        var repoRoot = Aspire.Templates.Tests.TestUtils.FindRepoRoot()?.FullName
            ?? throw new InvalidOperationException("Could not find repository root");

        return Path.Combine(repoRoot, relativePath);
    }
}
