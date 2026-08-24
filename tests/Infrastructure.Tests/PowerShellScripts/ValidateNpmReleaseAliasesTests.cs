// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;
using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>
/// Executes eng/scripts/validate-npm-release-aliases.ps1 (the canonical copy of the npm ESRP
/// owner/approver validation that the release pipeline mirrors inline) against sample inputs.
/// The script reads its inputs from environment variables, exactly like the release pipeline's
/// "Validate Parameters" step.
/// </summary>
public sealed class ValidateNpmReleaseAliasesTests
{
    private const string RequiredOwners = "joperezr,ankj";

    private readonly ITestOutputHelper _output;
    private readonly string _scriptPath;

    public ValidateNpmReleaseAliasesTests(ITestOutputHelper output)
    {
        _output = output;
        _scriptPath = Path.Combine(RepoRoot.Path, "eng", "scripts", "validate-npm-release-aliases.ps1");
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task FailsWhenOwnersHasNoAliases()
    {
        var result = await RunValidation(owners: "", approvers: "ankj");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "NpmPublishOwners must contain at least one alias before publishing npm packages.",
            Flatten(result.Output));
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task FailsWhenOwnersHasOnlyWhitespaceEntries()
    {
        var result = await RunValidation(owners: " , ", approvers: "ankj");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "NpmPublishOwners must contain at least one alias before publishing npm packages.",
            Flatten(result.Output));
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task FailsWhenApproversHasMultipleAliases()
    {
        var result = await RunValidation(owners: "joperezr", approvers: "ankj,octocat");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "NpmPublishApprovers must contain exactly one Microsoft alias or @microsoft.com email address.",
            Flatten(result.Output));
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task FailsWhenOwnersHasMultipleAliases()
    {
        var result = await RunValidation(owners: "joperezr,ankj", approvers: "adamratzman");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "NpmPublishOwners must contain exactly one Microsoft alias or @microsoft.com email address.",
            Flatten(result.Output));
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task FailsWhenOwnersMissingEveryRequiredAlias()
    {
        var result = await RunValidation(owners: "octocat", approvers: "ankj");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "NpmPublishOwners must include at least one required ESRP owner alias: ankj, joperezr.",
            Flatten(result.Output));
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task FailsWhenOwnerAndApproverOverlap()
    {
        var result = await RunValidation(owners: "joperezr", approvers: "joperezr");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "NpmPublishOwners and NpmPublishApprovers must not contain the same alias(es): joperezr.",
            Flatten(result.Output));
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task FailsWhenAliasIsNotAMicrosoftEmail()
    {
        var result = await RunValidation(owners: "joperezr@example.com", approvers: "ankj");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "NpmPublishOwners entry 'joperezr@example.com' must be a Microsoft alias or @microsoft.com email address.",
            Flatten(result.Output));
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task FailsWhenMicrosoftEmailHasEmptyAlias()
    {
        var result = await RunValidation(owners: "@microsoft.com", approvers: "ankj");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "NpmPublishOwners entry '@microsoft.com' must be a non-empty Microsoft alias or @microsoft.com email address containing only letters, digits, '.', '_' or '-'.",
            Flatten(result.Output));
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task FailsWhenAliasContainsNewlineLoggingCommand()
    {
        var maliciousApprover = "adamratzman\n##vso[task.setvariable variable=NpmPublishOwnersEffective]attacker";

        var result = await RunValidation(owners: "joperezr", approvers: maliciousApprover);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            @"NpmPublishApprovers entry 'adamratzman\n## vso[task.setvariable variable=NpmPublishOwnersEffective]attacker' must be a non-empty Microsoft alias or @microsoft.com email address containing only letters, digits, '.', '_' or '-'.",
            Flatten(result.Output));
        Assert.DoesNotContain("##vso[", result.Output);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task FailsWhenMicrosoftEmailSuffixWouldLeaveTrailingNewline()
    {
        var result = await RunValidation(owners: "joperezr,other\n@microsoft.com", approvers: "ankj");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            @"NpmPublishOwners entry 'other\n@microsoft.com' must be a non-empty Microsoft alias or @microsoft.com email address containing only letters, digits, '.', '_' or '-'.",
            Flatten(result.Output));
        Assert.DoesNotContain("variable=NpmPublishOwnersEffective", result.Output);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task StripsMicrosoftEmailSuffixFromOwnerAliases()
    {
        var result = await RunValidation(owners: "JOPEREZR@microsoft.com", approvers: "ankj");

        result.EnsureSuccessful();
        Assert.Contains("variable=NpmPublishOwnersEffective]joperezr", result.Output);
        Assert.Contains("variable=NpmPublishApproversEffective]ankj", result.Output);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task EmitsDeduplicatedEffectiveAliasesOnSuccess()
    {
        // Duplicate spellings of the same single owner alias (different casing and the
        // @microsoft.com suffix) collapse to one entry, satisfying the single-owner rule.
        var result = await RunValidation(owners: "joperezr,JOPEREZR,joperezr@microsoft.com", approvers: "adamratzman");

        result.EnsureSuccessful();
        Assert.Contains("variable=NpmPublishOwnersEffective]joperezr", result.Output);
        Assert.Contains("variable=NpmPublishApproversEffective]adamratzman", result.Output);
    }

    private async Task<CommandResult> RunValidation(string owners, string approvers, string requiredOwners = RequiredOwners)
    {
        using var cmd = new PowerShellCommand(_scriptPath, _output)
            .WithTimeout(TimeSpan.FromMinutes(2))
            .WithEnvironmentVariable("NPM_PUBLISH_OWNERS", owners)
            .WithEnvironmentVariable("NPM_PUBLISH_APPROVERS", approvers)
            .WithEnvironmentVariable("NPM_PUBLISH_REQUIRED_OWNERS", requiredOwners);

        return await cmd.ExecuteAsync();
    }

    // PowerShell's default ConciseView wraps Write-Error messages across multiple lines with
    // "|" gutters whose layout depends on the console width, and colorizes them with ANSI escape
    // sequences. Strip the escape sequences and gutters and collapse whitespace so assertions can
    // match the full message regardless of where it wrapped.
    private static string Flatten(string output)
    {
        // Matches ANSI SGR sequences such as ESC[31;1m that PowerShell emits when colorizing errors.
        var withoutAnsi = Regex.Replace(output, @"\u001b\[[0-9;]*m", string.Empty);
        return Regex.Replace(withoutAnsi.Replace("|", " "), @"\s+", " ");
    }

}
