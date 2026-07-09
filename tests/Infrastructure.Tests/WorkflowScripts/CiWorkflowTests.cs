// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;

namespace Infrastructure.Tests;

public sealed class CiWorkflowTests
{
    [Fact]
    public void RunTestsWorkflowInvokesDotnetExeDirectlyForWindowsTests()
    {
        var workflow = File.ReadAllText(Path.Combine(RepoRoot.Path, ".github", "workflows", "run-tests.yml"));
        var windowsRunTestsStep = System.Text.RegularExpressions.Regex.Match(workflow, "(?ms)^      - name: Run tests \\(Windows\\).*?(?=^      - |\\z)");
        Assert.True(windowsRunTestsStep.Success, "Could not find the Windows run-tests step in run-tests.yml.");

        Assert.Contains(@"\artifacts\toolset\sdk.txt", windowsRunTestsStep.Value);
        Assert.Contains("Join-Path $dotnetSdkDir \"dotnet.exe\"", windowsRunTestsStep.Value);
        Assert.Contains("Start-Process -FilePath $dotnetExecutable", windowsRunTestsStep.Value);
        Assert.Contains("& $dotnetExecutable test --project", windowsRunTestsStep.Value);
        Assert.DoesNotContain("& ${{ env.DOTNET_SCRIPT }} test", windowsRunTestsStep.Value);
    }

    [Fact]
    public void CiFailureTrackerCheckoutDoesNotPinMain()
    {
        var workflow = File.ReadAllText(Path.Combine(RepoRoot.Path, ".github", "workflows", "ci.yml"));
        var job = System.Text.RegularExpressions.Regex.Match(workflow, "(?ms)^  ci_failure_tracker:\\n(?<body>.*?)(?=^  [A-Za-z0-9_-]+:\\n|\\z)");
        Assert.True(job.Success, "Could not find the ci_failure_tracker job in ci.yml.");

        var checkout = System.Text.RegularExpressions.Regex.Match(job.Value, "(?ms)^      - uses: actions/checkout@.*?(?=^      - |\\z)");
        Assert.True(checkout.Success, "Could not find the ci_failure_tracker checkout step.");

        // Push CI also runs on release/**. Pinning this checkout to main makes the
        // tracker execute main's reporter instead of the workflow code from the branch
        // whose run is being evaluated.
        Assert.DoesNotContain("ref: main", checkout.Value);
    }
}
