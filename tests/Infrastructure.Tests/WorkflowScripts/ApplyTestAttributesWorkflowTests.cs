// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;

namespace Infrastructure.Tests;

public sealed class ApplyTestAttributesWorkflowTests
{
    [Fact]
    public void WorkflowHelperCheckoutDoesNotPoisonRepositoryCheckout()
    {
        var workflow = File.ReadAllText(Path.Combine(RepoRoot.Path, ".github", "workflows", "apply-test-attributes.yml")).ReplaceLineEndings("\n");

        Assert.Equal(
            string.Join('\n',
            [
                "        path: .workflow-helpers",
                "        sparse-checkout: .github/workflows/workflow-command-helpers.js",
                "        sparse-checkout-cone-mode: false",
                "        persist-credentials: false"
            ]),
            MatchStepInputs(workflow, "Checkout workflow helpers"));

        Assert.Equal(
            "const { tokenizeArguments } = require(`${process.env.GITHUB_WORKSPACE}/.workflow-helpers/.github/workflows/workflow-command-helpers.js`);",
            MatchLineContaining(workflow, "const { tokenizeArguments } = require(").Trim());

        Assert.Equal(
            string.Join('\n',
            [
                "        ref: ${{ steps.determine-target.outputs.checkout_ref }}",
                "        fetch-depth: 0",
                "        token: ${{ steps.app-token.outputs.token }}"
            ]),
            MatchStepInputs(workflow, "Checkout repo"));
    }

    private static string MatchStepInputs(string workflow, string stepName)
    {
        var step = MatchStep(workflow, stepName);
        var match = System.Text.RegularExpressions.Regex.Match(step, "(?ms)^      with:\n(?<inputs>.*)\\z");

        Assert.True(match.Success, $"Could not find the '{stepName}' inputs.");

        return match.Groups["inputs"].Value.TrimEnd();
    }

    private static string MatchStep(string workflow, string stepName)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            workflow,
            $@"(?ms)^    - name: {System.Text.RegularExpressions.Regex.Escape(stepName)}\n(?<body>.*?)(?=^    - name: |\z)");

        Assert.True(match.Success, $"Could not find the '{stepName}' step.");

        return match.Value;
    }

    private static string MatchLineContaining(string text, string marker)
    {
        var lines = text.Split('\n').Where(line => line.Contains(marker, StringComparison.Ordinal)).ToArray();

        Assert.Single(lines);

        return lines[0];
    }
}
