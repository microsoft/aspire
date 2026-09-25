// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace Aspire.Templates.Tests;

public partial class LocalhostTldHostnameTests(ITestOutputHelper testOutput) : TemplateTestsBase(testOutput)
{
    [GeneratedRegex(@"://([^:]+)\.dev\.localhost:")]
    private static partial Regex HostnamePattern();

    public static TheoryData<string, string, string, TestTargetFramework> LocalhostTldHostname_TestData()
    {
        TheoryData<string, string, string, TestTargetFramework> data = [];

        foreach (var framework in new[] { TestTargetFramework.Net10, TestTargetFramework.Net11 })
        {
            data.Add("aspire", "my.namespace.app", "my-namespace-app", framework);
            data.Add("aspire", ".StartWithDot", "startwithdot", framework);
            data.Add("aspire", "EndWithDot.", "endwithdot", framework);
            data.Add("aspire", "My..Test__Project", "my-test-project", framework);
            data.Add("aspire", "Project123.Test456", "project123-test456", framework);
            data.Add("aspire-apphost", "my.service.name", "my-service-name", framework);
            data.Add("aspire-apphost-singlefile", "-my.service..name-", "my-service-name", framework);
            data.Add("aspire-starter", "Test_App.1", "test-app-1", framework);
            data.Add("aspire-ts-cs-starter", "My-App.Test", "my-app-test", framework);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(LocalhostTldHostname_TestData))]
    [Trait("category", "basic-build")]
    public async Task LocalhostTld_GeneratesDnsCompliantHostnames(string templateName, string projectName, string expectedHostname, TestTargetFramework framework)
    {
        var id = GetNewProjectId(prefix: $"localhost_tld_{templateName}");

        var buildEnvironment = framework switch
        {
            TestTargetFramework.Net10 => BuildEnvironment.ForNet10SdkOnly,
            TestTargetFramework.Net11 => BuildEnvironment.ForNet11SdkOnly,
            _ => throw new ArgumentOutOfRangeException(nameof(framework))
        };

        // Single-file AppHosts have no project file for the helper to validate, so pass -f directly.
        var targetFramework = templateName == "aspire-apphost-singlefile" ? TestTargetFramework.None : framework;
        var frameworkArg = templateName == "aspire-apphost-singlefile" ? $"-f {framework.ToTFMString()} " : "";

        await using var project = await AspireProject.CreateNewTemplateProjectAsync(
            id,
            templateName,
            _testOutput,
            buildEnvironment: buildEnvironment,
            extraArgs: $"{frameworkArg}--localhost-tld -n \"{projectName}\"",
            targetFramework,
            addEndpointsHook: false); // Don't add endpoint hook since we're just checking file generation

        // When using -n, the template still outputs to the -o directory (id),
        // but the project names inside use the -n value
        // Find the launchSettings.json file - it will be in a directory named after the project
        var launchSettingsPath = templateName switch
        {
            "aspire-ts-cs-starter" or "aspire-starter" => Path.Combine(project.RootDir, $"{projectName}.AppHost", "Properties", "launchSettings.json"),
            "aspire" => Path.Combine(project.RootDir, $"{projectName}.AppHost", "Properties", "launchSettings.json"),
            "aspire-apphost" => Path.Combine(project.RootDir, "Properties", "launchSettings.json"),
            "aspire-apphost-singlefile" => Path.Combine(project.RootDir, "apphost.run.json"),
            _ => throw new ArgumentException($"Unknown template: {templateName}")
        };

        Assert.True(File.Exists(launchSettingsPath), $"launchSettings.json/apphost.run.json file not found at {launchSettingsPath}");

        var launchSettingsContent = await File.ReadAllTextAsync(launchSettingsPath);
        using var launchSettings = JsonDocument.Parse(launchSettingsContent);

        var profiles = launchSettings.RootElement.GetProperty("profiles");

        var foundDevLocalhost = false;
        foreach (var profile in profiles.EnumerateObject())
        {
            if (profile.Value.TryGetProperty("applicationUrl", out var applicationUrl))
            {
                var urls = applicationUrl.GetString();
                if (!string.IsNullOrEmpty(urls) && urls.Contains(".dev.localhost:"))
                {
                    foundDevLocalhost = true;

                    // Verify the hostname in the URL matches expected DNS-compliant format
                    Assert.Contains($"{expectedHostname}.dev.localhost:", urls);

                    // Verify no underscores in hostname (RFC 952/1123 compliance)
                    var matches = HostnamePattern().Matches(urls);
                    foreach (Match match in matches)
                    {
                        var hostname = match.Groups[1].Value;
                        Assert.DoesNotContain("_", hostname, StringComparison.Ordinal);
                        Assert.DoesNotContain(".", hostname, StringComparison.Ordinal);
                        Assert.False(hostname.StartsWith("-", StringComparison.Ordinal),
                            $"Hostname '{hostname}' should not start with hyphen (RFC 952/1123 violation)");
                        Assert.False(hostname.EndsWith("-", StringComparison.Ordinal),
                            $"Hostname '{hostname}' should not end with hyphen (RFC 952/1123 violation)");
                    }
                }
            }
        }

        Assert.True(foundDevLocalhost, "No .dev.localhost URLs found in launchSettings.json");
    }
}
