// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Cli.Acquisition;
using Aspire.Cli.Interaction;
using Aspire.Cli.Projects;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Utils;
using Aspire.Cli.Utils.EnvironmentChecker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.InternalTesting;
using Spectre.Console;

namespace Aspire.Cli.Tests.Commands;

public class DoctorCommandTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task DoctorCommand_SelfJson_PreservesLegacyPeerContract()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var outputWriter = new TestOutputTextWriter(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.OutputTextWriter = outputWriter;
        });
        services.RemoveAll<IInstallationDiscovery>();
        services.AddSingleton<IInstallationDiscovery>(new FakeInstallationDiscovery(new InstallationInfo
        {
            Path = "/test/aspire",
            Version = "13.5.0",
            Channel = "staging",
            Route = "script",
            PathStatus = InstallationPathStatus.Active,
            Status = InstallationInfoStatus.Ok,
        }));
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>();
        var result = command.Parse("doctor --self --format json");
        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        using var document = JsonDocument.Parse(string.Concat(outputWriter.Logs));
        var installation = Assert.Single(document.RootElement.GetProperty("installations").EnumerateArray());
        Assert.Equal("/test/aspire", installation.GetProperty("path").GetString());
        Assert.Equal("13.5.0", installation.GetProperty("version").GetString());
        Assert.Equal("staging", installation.GetProperty("channel").GetString());
        Assert.Equal("script", installation.GetProperty("route").GetString());
    }

    [Fact]
    public async Task DoctorCommand_Help_Works()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>();
        var result = command.Parse("doctor --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        
        // Help should return success
        Assert.Equal(CliExitCodes.Success, exitCode);
    }

    [Fact]
    public async Task DoctorCommand_Json_IncludesCliVersionStatus()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var doc = await RunDoctorJsonAsync(workspace,
            configureOptions: options =>
            {
                options.CliUpdateNotifierFactory = _ => new TestCliUpdateNotifier
                {
                    GetVersionStatusAsyncCallback = (_, _) => Task.FromResult(new CliVersionStatus("13.0.0", "13.1.0", "aspire update"))
                };
            });

        var cliVersionCheck = GetCheckByName(doc, AspireVersionCheck.CliVersionCheckName);
        Assert.Equal(EnvironmentCheckCategories.Aspire, cliVersionCheck.GetProperty("category").GetString());
        Assert.Equal("warning", cliVersionCheck.GetProperty("status").GetString());
        Assert.Contains("13.0.0", cliVersionCheck.GetProperty("message").GetString()!);
        Assert.Contains("13.1.0", cliVersionCheck.GetProperty("message").GetString()!);
        var cliVersionMetadata = cliVersionCheck.GetProperty("metadata");
        Assert.Equal("13.0.0", cliVersionMetadata.GetProperty("currentVersion").GetString());
        Assert.Equal("13.1.0", cliVersionMetadata.GetProperty("latestVersion").GetString());
        Assert.Equal("aspire update", cliVersionMetadata.GetProperty("updateCommand").GetString());
    }

    [Fact]
    public async Task DoctorCommand_Json_IncludesOperatingSystemStatus()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var doc = await RunDoctorJsonAsync(workspace,
            configureOptions: options =>
            {
                options.CliUpdateNotifierFactory = _ => new TestCliUpdateNotifier();
            });

        var osCheck = GetCheckByName(doc, OperatingSystemCheck.CheckName);
        Assert.Equal(EnvironmentCheckCategories.Environment, osCheck.GetProperty("category").GetString());
        Assert.Equal("pass", osCheck.GetProperty("status").GetString());
        Assert.StartsWith("Operating system: ", osCheck.GetProperty("message").GetString(), StringComparison.Ordinal);
        var metadata = osCheck.GetProperty("metadata");
        Assert.True(metadata.TryGetProperty("osType", out _));
        Assert.True(metadata.TryGetProperty("displayName", out _));
        Assert.True(metadata.TryGetProperty("version", out _));
    }

    [Fact]
    [SkipOnPlatform(TestPlatforms.Windows | TestPlatforms.OSX | TestPlatforms.FreeBSD, "Validates Linux /etc/os-release values.")]
    public async Task DoctorCommand_Json_OnLinux_UsesOsReleaseValues()
    {
        Assert.SkipUnless(File.Exists("/etc/os-release"), "Linux /etc/os-release is required for this test.");

        var osRelease = OperatingSystemCheck.ParseLinuxOsRelease(
            await File.ReadAllTextAsync("/etc/os-release", TestContext.Current.CancellationToken));
        Assert.True(TryGetOsReleaseValue(osRelease, "NAME", out var name), "Expected /etc/os-release to include NAME.");

        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var doc = await RunDoctorJsonAsync(workspace,
            configureOptions: options =>
            {
                options.CliUpdateNotifierFactory = _ => new TestCliUpdateNotifier();
            });

        var osCheck = GetCheckByName(doc, OperatingSystemCheck.CheckName);
        var metadata = osCheck.GetProperty("metadata");
        var expectedDisplayName = GetExpectedLinuxDisplayName(name);

        Assert.Equal("Linux", metadata.GetProperty("osType").GetString());
        Assert.Equal(expectedDisplayName, metadata.GetProperty("displayName").GetString());

        if (TryGetOsReleaseValue(osRelease, "VERSION_ID", out var version))
        {
            Assert.Equal(version, metadata.GetProperty("version").GetString());
            Assert.Equal($"Operating system: {expectedDisplayName} {version}", osCheck.GetProperty("message").GetString());
        }

        if (TryGetOsReleaseValue(osRelease, "PRETTY_NAME", out var prettyName))
        {
            Assert.Equal(prettyName, metadata.GetProperty("description").GetString());
        }
    }

    [Fact]
    public async Task DoctorCommand_Json_VersionUpdateBanner_IsSuppressed()
    {
        // The cli-version environment check already surfaces "newer version available" inside
        // checks[]; the post-command update banner would be a second, less-structured copy of
        // the same data. DoctorCommand opts out of BaseCommand's update notifier
        // (UpdateNotificationsEnabled => false) so the banner does not fire at all — neither
        // on stdout (which would break JSON parsing) nor on stderr (where it would just be noise
        // duplicating checks[].cli-version).
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var outputWriter = new TestOutputTextWriter(outputHelper);
        var errorWriter = new StringWriter();
        var notifierInvoked = false;

        var services = CreateDoctorVersionServiceCollection(workspace, outputHelper, options =>
        {
            options.OutputTextWriter = outputWriter;
            options.ErrorTextWriter = errorWriter;
            options.CliUpdateNotifierFactory = sp => new TestCliUpdateNotifier
            {
                NotifyIfUpdateAvailableCallback = () =>
                {
                    notifierInvoked = true;
                    var interactionService = sp.GetRequiredService<IInteractionService>();
                    interactionService.DisplayVersionUpdateNotification("13.99.0", "aspire update");
                }
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>();
        var result = command.Parse("doctor --format json");
        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(CliExitCodes.Success, exitCode);

        Assert.False(notifierInvoked, "DoctorCommand should not invoke the CLI update notifier; the cli-version check carries that information directly in checks[].");

        var stdoutText = string.Concat(outputWriter.Logs);
        using var doc = JsonDocument.Parse(stdoutText);
        Assert.True(doc.RootElement.TryGetProperty("checks", out _));

        var stderrText = errorWriter.ToString();
        Assert.DoesNotContain("13.99.0", stderrText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DoctorCommand_Json_IncludesAppHostVersionWhenAppHostExists()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj"));
        await File.WriteAllTextAsync(appHostFile.FullName, "<Project />");

        using var doc = await RunDoctorJsonAsync(workspace,
            configureOptions: options =>
            {
                options.CliUpdateNotifierFactory = _ => new TestCliUpdateNotifier();
                options.AppHostProjectFactory = _ => new TestAppHostProjectFactory
                {
                    GetAspireHostingVersionAsyncCallback = (_, _) => Task.FromResult<string?>("13.0.0")
                };
            });

        var appHostVersionCheck = GetCheckByName(doc, AspireVersionCheck.AppHostVersionCheckName);
        Assert.Equal(EnvironmentCheckCategories.AppHost, appHostVersionCheck.GetProperty("category").GetString());
        Assert.Equal("pass", appHostVersionCheck.GetProperty("status").GetString());
        Assert.Contains("13.0.0", appHostVersionCheck.GetProperty("message").GetString()!);
        Assert.Contains("AppHost.csproj", appHostVersionCheck.GetProperty("message").GetString()!);
        var appHostVersionMetadata = appHostVersionCheck.GetProperty("metadata");
        Assert.Equal("13.0.0", appHostVersionMetadata.GetProperty("version").GetString());
        Assert.Equal("AppHost.csproj", appHostVersionMetadata.GetProperty("appHostPath").GetString());
    }

    [Fact]
    public async Task DoctorCommand_Json_IncludesTypeScriptAppHostVersionFromAspireConfig()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.ts"));
        await File.WriteAllTextAsync(appHostFile.FullName, "export {};");
        await File.WriteAllTextAsync(
            Path.Combine(workspace.WorkspaceRoot.FullName, "aspire.config.json"),
            """
            {
              "sdk": {
                "version": "13.1.0"
              }
            }
            """);

        var runnerCalled = false;
        using var doc = await RunDoctorJsonAsync(workspace,
            configureOptions: options =>
            {
                options.CliUpdateNotifierFactory = _ => new TestCliUpdateNotifier();
                options.AppHostProjectFactory = _ => new TestAppHostProjectFactory
                {
                    CanHandleCallback = file => file.Extension.Equals(".ts", StringComparison.OrdinalIgnoreCase),
                    DetectionPatterns = ["apphost.ts"],
                    GetAspireHostingVersionAsyncCallback = (_, _) => Task.FromResult<string?>("13.1.0")
                };
                options.DotNetCliRunnerFactory = _ => new TestDotNetCliRunner
                {
                    GetAppHostInformationAsyncCallback = (_, _, _) =>
                    {
                        runnerCalled = true;
                        return (0, true, "unexpected");
                    }
                };
            });

        Assert.False(runnerCalled);

        var appHostVersionCheck = GetCheckByName(doc, AspireVersionCheck.AppHostVersionCheckName);
        Assert.Equal(EnvironmentCheckCategories.AppHost, appHostVersionCheck.GetProperty("category").GetString());
        Assert.Equal("pass", appHostVersionCheck.GetProperty("status").GetString());
        Assert.Contains("13.1.0", appHostVersionCheck.GetProperty("message").GetString()!);
        Assert.Contains("apphost.ts", appHostVersionCheck.GetProperty("message").GetString()!);
        var appHostVersionMetadata = appHostVersionCheck.GetProperty("metadata");
        Assert.Equal("13.1.0", appHostVersionMetadata.GetProperty("version").GetString());
        Assert.Equal("apphost.ts", appHostVersionMetadata.GetProperty("appHostPath").GetString());
    }

    [Fact]
    public async Task DoctorCommand_Json_DoesNotDiscoverNestedAppHostWithoutConfig()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostFile = CreateDeepAppHostFile(workspace, depth: LanguageInfo.DetectionRecurseLimit + 1);
        await File.WriteAllTextAsync(appHostFile.FullName, "<Project />");

        var versionLookupCalled = false;
        using var doc = await RunDoctorJsonAsync(workspace,
            configureOptions: options =>
            {
                options.CliUpdateNotifierFactory = _ => new TestCliUpdateNotifier();
                options.AppHostProjectFactory = _ => new TestAppHostProjectFactory
                {
                    GetAspireHostingVersionAsyncCallback = (_, _) =>
                    {
                        versionLookupCalled = true;
                        return Task.FromResult<string?>("unexpected");
                    }
                };
            });

        Assert.False(versionLookupCalled);
        Assert.DoesNotContain(doc.RootElement.GetProperty("checks").EnumerateArray(),
            check => check.GetProperty("name").GetString() == AspireVersionCheck.AppHostVersionCheckName);
    }

    [Fact]
    public async Task DoctorCommand_Json_DoesNotShowAppHostVersionForNonAppHostProject()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var projectFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "Normal.csproj"));
        await File.WriteAllTextAsync(projectFile.FullName, "<Project />");

        var versionLookupCalled = false;
        using var doc = await RunDoctorJsonAsync(workspace,
            configureOptions: options =>
            {
                options.CliUpdateNotifierFactory = _ => new TestCliUpdateNotifier();
                options.AppHostProjectFactory = _ => new TestAppHostProjectFactory
                {
                    ValidateAppHostCallback = _ => new AppHostValidationResult(IsValid: false),
                    GetAspireHostingVersionAsyncCallback = (_, _) =>
                    {
                        versionLookupCalled = true;
                        return Task.FromResult<string?>("unexpected");
                    }
                };
            });

        Assert.False(versionLookupCalled);
        Assert.DoesNotContain(doc.RootElement.GetProperty("checks").EnumerateArray(),
            check => check.GetProperty("name").GetString() == AspireVersionCheck.AppHostVersionCheckName);
    }

    [Fact]
    public async Task DoctorCommand_Json_DoesNotDiscoverNestedAppHostWhenAnotherProjectExists()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var projectFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "Normal.csproj"));
        await File.WriteAllTextAsync(projectFile.FullName, "<Project />");
        var appHostDirectory = workspace.WorkspaceRoot.CreateSubdirectory("app");
        var appHostFile = new FileInfo(Path.Combine(appHostDirectory.FullName, "AppHost.csproj"));
        await File.WriteAllTextAsync(appHostFile.FullName, "<Project />");

        using var doc = await RunDoctorJsonAsync(workspace,
            configureOptions: options =>
            {
                options.CliUpdateNotifierFactory = _ => new TestCliUpdateNotifier();
                options.AppHostProjectFactory = _ => new TestAppHostProjectFactory
                {
                    ValidateAppHostCallback = file => new AppHostValidationResult(
                        IsValid: file.Name.Equals("AppHost.csproj", StringComparison.OrdinalIgnoreCase)),
                    GetAspireHostingVersionAsyncCallback = (_, _) => Task.FromResult<string?>("13.2.0")
                };
            });

        Assert.DoesNotContain(doc.RootElement.GetProperty("checks").EnumerateArray(),
            check => check.GetProperty("name").GetString() == AspireVersionCheck.AppHostVersionCheckName);
    }

    [Fact]
    public async Task DoctorCommand_Json_DoesNotChooseBetweenMultipleDirectAppHostsWithoutConfig()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await File.WriteAllTextAsync(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj"), "<Project />");
        await File.WriteAllTextAsync(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.fsproj"), "<Project />");

        var versionLookupCalled = false;
        using var doc = await RunDoctorJsonAsync(workspace,
            configureOptions: options =>
            {
                options.CliUpdateNotifierFactory = _ => new TestCliUpdateNotifier();
                options.AppHostProjectFactory = _ => new TestAppHostProjectFactory
                {
                    GetAspireHostingVersionAsyncCallback = (_, _) =>
                    {
                        versionLookupCalled = true;
                        return Task.FromResult<string?>("unexpected");
                    }
                };
            });

        Assert.False(versionLookupCalled);
        Assert.DoesNotContain(doc.RootElement.GetProperty("checks").EnumerateArray(),
            check => check.GetProperty("name").GetString() == AspireVersionCheck.AppHostVersionCheckName);
    }

    [Fact]
    public async Task DoctorCommand_Json_PreservesCliVersionWhenAppHostVersionResolutionFails()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.ts"));
        await File.WriteAllTextAsync(appHostFile.FullName, "export {};");

        using var doc = await RunDoctorJsonAsync(workspace,
            configureOptions: options =>
            {
                options.CliUpdateNotifierFactory = _ => new TestCliUpdateNotifier();
                options.AppHostProjectFactory = _ => new TestAppHostProjectFactory
                {
                    CanHandleCallback = file => file.Extension.Equals(".ts", StringComparison.OrdinalIgnoreCase),
                    DetectionPatterns = ["apphost.ts"],
                    GetAspireHostingVersionAsyncCallback = (_, _) =>
                        throw new InvalidOperationException("invalid aspire.config.json")
                };
            });

        var cliVersionCheck = GetCheckByName(doc, AspireVersionCheck.CliVersionCheckName);
        var appHostVersionCheck = GetCheckByName(doc, AspireVersionCheck.AppHostVersionCheckName);

        Assert.Equal("pass", cliVersionCheck.GetProperty("status").GetString());
        Assert.Equal("warning", appHostVersionCheck.GetProperty("status").GetString());
        Assert.Equal("invalid aspire.config.json", appHostVersionCheck.GetProperty("details").GetString());
        Assert.Equal(
            "apphost.ts",
            appHostVersionCheck.GetProperty("metadata").GetProperty("appHostPath").GetString());
    }

    [Fact]
    public async Task DoctorCommand_Json_PreservesCliVersionWhenAppHostDiscoveryFails()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        using var doc = await RunDoctorJsonAsync(workspace,
            configureOptions: options =>
            {
                options.CliUpdateNotifierFactory = _ => new TestCliUpdateNotifier();
                options.ProjectLocatorFactory = _ => new TestProjectLocator
                {
                    GetAppHostFromSettingsAsyncCallback = _ => throw new IOException("settings lookup failed")
                };
            });

        var cliVersionCheck = GetCheckByName(doc, AspireVersionCheck.CliVersionCheckName);
        var appHostVersionCheck = GetCheckByName(doc, AspireVersionCheck.AppHostVersionCheckName);

        Assert.Equal("pass", cliVersionCheck.GetProperty("status").GetString());
        Assert.Equal("warning", appHostVersionCheck.GetProperty("status").GetString());
        Assert.Equal("settings lookup failed", appHostVersionCheck.GetProperty("details").GetString());
    }

    [Fact]
    public async Task DoctorCommand_Json_UsesConfiguredAppHostBeyondLanguageDetectionLimit()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostFile = CreateDeepAppHostFile(workspace, depth: LanguageInfo.DetectionRecurseLimit + 1);
        await File.WriteAllTextAsync(appHostFile.FullName, "<Project />");
        await File.WriteAllTextAsync(
            Path.Combine(workspace.WorkspaceRoot.FullName, "aspire.config.json"),
            $$"""
            {
              "appHost": {
                "path": "{{Path.GetRelativePath(workspace.WorkspaceRoot.FullName, appHostFile.FullName).Replace('\\', '/')}}"
              }
            }
            """);

        using var doc = await RunDoctorJsonAsync(workspace,
            configureOptions: options =>
            {
                options.CliUpdateNotifierFactory = _ => new TestCliUpdateNotifier();
                options.AppHostProjectFactory = _ => new TestAppHostProjectFactory
                {
                    GetAspireHostingVersionAsyncCallback = (_, _) => Task.FromResult<string?>("13.2.0")
                };
            });

        var appHostVersionCheck = GetCheckByName(doc, AspireVersionCheck.AppHostVersionCheckName);
        Assert.Equal(EnvironmentCheckCategories.AppHost, appHostVersionCheck.GetProperty("category").GetString());
        Assert.Equal("pass", appHostVersionCheck.GetProperty("status").GetString());
        Assert.Contains("13.2.0", appHostVersionCheck.GetProperty("message").GetString()!);
        Assert.Contains("AppHost.csproj", appHostVersionCheck.GetProperty("message").GetString()!);
        var appHostVersionMetadata = appHostVersionCheck.GetProperty("metadata");
        Assert.Equal("13.2.0", appHostVersionMetadata.GetProperty("version").GetString());
        Assert.Equal(
            Path.Combine("level0", "level1", "level2", "level3", "level4", "level5", "AppHost.csproj"),
            appHostVersionMetadata.GetProperty("appHostPath").GetString());
    }

    [Fact]
    public async Task DoctorCommand_Json_CliVersion_IncludesIdentityChannelFromReader()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        // Override the channel reader registered by CliTestHelper with a fake
        // returning a deterministic value, so the assertion is not coupled to
        // whichever channel the test host's Aspire.Cli assembly happens to bake in.
        using var doc = await RunDoctorJsonAsync(workspace,
            configureOptions: options =>
            {
                options.CliUpdateNotifierFactory = _ => new TestCliUpdateNotifier
                {
                    GetVersionStatusAsyncCallback = (_, _) => Task.FromResult(new CliVersionStatus("13.0.0", LatestVersion: null, UpdateCommand: null))
                };
            },
            configureServices: services =>
            {
                services.RemoveAll<IIdentityChannelReader>();
                services.AddSingleton<IIdentityChannelReader>(_ => new FakeIdentityChannelReader("staging"));
            });

        var cliVersionCheck = GetCheckByName(doc, AspireVersionCheck.CliVersionCheckName);
        var metadata = cliVersionCheck.GetProperty("metadata");
        Assert.Equal("staging", metadata.GetProperty("identityChannel").GetString());
        Assert.Contains("channel: staging", cliVersionCheck.GetProperty("message").GetString()!);
    }

    [Fact]
    public async Task DoctorCommand_Json_CliVersion_OmitsIdentityChannelWhenReaderThrows()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var doc = await RunDoctorJsonAsync(workspace,
            configureOptions: options =>
            {
                options.CliUpdateNotifierFactory = _ => new TestCliUpdateNotifier
                {
                    GetVersionStatusAsyncCallback = (_, _) => Task.FromResult(new CliVersionStatus("13.0.0", LatestVersion: null, UpdateCommand: null))
                };
            },
            configureServices: services =>
            {
                services.RemoveAll<IIdentityChannelReader>();
                // Throws to simulate a misconfigured dev build with no AspireCliChannel metadata.
                services.AddSingleton<IIdentityChannelReader>(_ => new FakeIdentityChannelReader(failOnRead: true));
            });

        // The channel lookup failing is informational; the rest of doctor should still complete.
        var cliVersionCheck = GetCheckByName(doc, AspireVersionCheck.CliVersionCheckName);
        var metadata = cliVersionCheck.GetProperty("metadata");
        Assert.False(metadata.TryGetProperty("identityChannel", out _));
        Assert.DoesNotContain("channel:", cliVersionCheck.GetProperty("message").GetString()!);
    }

    [Fact]
    public async Task DoctorCommand_Json_AppHostVersion_IncludesPinnedChannelFromAspireConfig()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj"));
        await File.WriteAllTextAsync(appHostFile.FullName, "<Project />");
        await File.WriteAllTextAsync(
            Path.Combine(workspace.WorkspaceRoot.FullName, "aspire.config.json"),
            """
            {
              "channel": "daily"
            }
            """);

        using var doc = await RunDoctorJsonAsync(workspace,
            configureOptions: options =>
            {
                options.CliUpdateNotifierFactory = _ => new TestCliUpdateNotifier();
                options.AppHostProjectFactory = _ => new TestAppHostProjectFactory
                {
                    GetAspireHostingVersionAsyncCallback = (_, _) => Task.FromResult<string?>("13.0.0")
                };
            });

        var appHostVersionCheck = GetCheckByName(doc, AspireVersionCheck.AppHostVersionCheckName);
        var metadata = appHostVersionCheck.GetProperty("metadata");
        Assert.Equal("daily", metadata.GetProperty("pinnedChannel").GetString());
        Assert.Contains("channel: daily", appHostVersionCheck.GetProperty("message").GetString()!);
    }

    [Fact]
    public async Task DoctorCommand_Json_AppHostVersion_IncludesPinnedChannelFromAspireConfigWhenAppHostIsNested()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var nestedAppHostDir = workspace.WorkspaceRoot.CreateSubdirectory("src").CreateSubdirectory("NestedAppHost");
        var appHostFile = new FileInfo(Path.Combine(nestedAppHostDir.FullName, "AppHost.csproj"));
        await File.WriteAllTextAsync(appHostFile.FullName, "<Project />");
        await File.WriteAllTextAsync(
            Path.Combine(workspace.WorkspaceRoot.FullName, "aspire.config.json"),
            """
            {
              "appHost": {
                "path": "src/NestedAppHost/AppHost.csproj"
              },
              "channel": "daily"
            }
            """);

        using var doc = await RunDoctorJsonAsync(workspace,
            configureOptions: options =>
            {
                options.CliUpdateNotifierFactory = _ => new TestCliUpdateNotifier();
                options.AppHostProjectFactory = _ => new TestAppHostProjectFactory
                {
                    GetAspireHostingVersionAsyncCallback = (_, _) => Task.FromResult<string?>("13.0.0")
                };
            });

        var appHostVersionCheck = GetCheckByName(doc, AspireVersionCheck.AppHostVersionCheckName);
        var metadata = appHostVersionCheck.GetProperty("metadata");
        Assert.Equal("daily", metadata.GetProperty("pinnedChannel").GetString());
        Assert.Contains("channel: daily", appHostVersionCheck.GetProperty("message").GetString()!);
        Assert.Equal(Path.Combine("src", "NestedAppHost", "AppHost.csproj"), metadata.GetProperty("appHostPath").GetString());
    }

    [Fact]
    public async Task DoctorCommand_Json_AppHostVersion_OmitsPinnedChannelWhenAspireConfigAbsent()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj"));
        await File.WriteAllTextAsync(appHostFile.FullName, "<Project />");
        // Intentionally no aspire.config.json — verifies the lookup degrades silently.

        using var doc = await RunDoctorJsonAsync(workspace,
            configureOptions: options =>
            {
                options.CliUpdateNotifierFactory = _ => new TestCliUpdateNotifier();
                options.AppHostProjectFactory = _ => new TestAppHostProjectFactory
                {
                    GetAspireHostingVersionAsyncCallback = (_, _) => Task.FromResult<string?>("13.0.0")
                };
            });

        var appHostVersionCheck = GetCheckByName(doc, AspireVersionCheck.AppHostVersionCheckName);
        var metadata = appHostVersionCheck.GetProperty("metadata");
        Assert.False(metadata.TryGetProperty("pinnedChannel", out _));
        Assert.DoesNotContain("channel:", appHostVersionCheck.GetProperty("message").GetString()!);
    }

    [Fact]
    public async Task DoctorCommand_Json_CliVersion_IncludesLatestVersionChannel_WhenUpdateAvailable()
    {
        // When an update is available, doctor should surface BOTH channel
        // labels — identityChannel for the running CLI, latestVersionChannel
        // for the recommendation lane (stable vs prerelease) — so the user
        // can see exactly where the recommendation is being pulled from.
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var doc = await RunDoctorJsonAsync(workspace,
            configureOptions: options =>
            {
                options.CliUpdateNotifierFactory = _ => new TestCliUpdateNotifier
                {
                    GetVersionStatusAsyncCallback = (_, _) => Task.FromResult(new CliVersionStatus(
                        CurrentVersion: "13.4.0-dev",
                        LatestVersion: "13.4.0-preview.1.26264.8",
                        UpdateCommand: "aspire update",
                        UpdateCheckError: null,
                        LatestVersionChannel: "prerelease"))
                };
            },
            configureServices: services =>
            {
                services.RemoveAll<IIdentityChannelReader>();
                services.AddSingleton<IIdentityChannelReader>(_ => new FakeIdentityChannelReader("local"));
            });

        var cliVersionCheck = GetCheckByName(doc, AspireVersionCheck.CliVersionCheckName);

        // Both channels surface in metadata.
        var metadata = cliVersionCheck.GetProperty("metadata");
        Assert.Equal("local", metadata.GetProperty("identityChannel").GetString());
        Assert.Equal("prerelease", metadata.GetProperty("latestVersionChannel").GetString());

        // The human-readable message attaches the channel to each version
        // it qualifies. Both must appear at well-defined positions so the
        // user can't mis-read which channel is which.
        var message = cliVersionCheck.GetProperty("message").GetString()!;
        var currentIdx = message.IndexOf("13.4.0-dev (channel: local)", StringComparison.Ordinal);
        var latestIdx = message.IndexOf("13.4.0-preview.1.26264.8 (channel: prerelease)", StringComparison.Ordinal);
        Assert.True(currentIdx >= 0, $"Expected current version with channel suffix in message; got: {message}");
        Assert.True(latestIdx >= 0, $"Expected latest version with channel suffix in message; got: {message}");
        Assert.True(currentIdx < latestIdx, "Current version must appear before latest version in message.");
    }

    [Fact]
    public async Task DoctorCommand_Json_ContainsOnlyChecksAndSummary()
    {
        // Installation discovery is exclusively owned by `aspire --info` now.
        // `aspire doctor --format json` must never carry an "installations"
        // property, and its only top-level properties are "checks" and "summary".
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var doc = await RunDoctorJsonAsync(workspace,
            configureOptions: options =>
            {
                options.CliUpdateNotifierFactory = _ => new TestCliUpdateNotifier();
            });

        Assert.False(doc.RootElement.TryGetProperty("installations", out _));

        var topLevelPropertyNames = doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(["checks", "summary"], topLevelPropertyNames);
    }

    [Fact]
    public async Task DoctorCommand_HumanReadable_EndsWithHealthSummary()
    {
        // Installation discovery is exclusively owned by `aspire --info` now;
        // doctor's human-readable output ends with the health summary line and
        // never appends an installation inventory section afterward.
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var output = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No,
            Out = new AnsiConsoleOutput(output),
            Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false },
        });
        console.Profile.Width = int.MaxValue;

        var services = CreateDoctorVersionServiceCollection(workspace, outputHelper, options =>
        {
            options.CliUpdateNotifierFactory = _ => new TestCliUpdateNotifier();
        });
        services.RemoveAll<IAnsiConsole>();
        services.AddSingleton<IAnsiConsole>(console);

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>();
        var result = command.Parse("doctor");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        var rendered = output.ToString();
        var lines = rendered.Split(
            Environment.NewLine,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Assert.StartsWith("Summary:", lines[^1], StringComparison.Ordinal);
    }

    // Centralizes the scaffolding shared by `doctor --format json` tests:
    // build services via CreateDoctorVersionServiceCollection wired to a
    // TextWriter capturing the real stdout sink, optionally tweak the
    // registered services (e.g. swap IIdentityChannelReader), run the
    // requested doctor command, assert success, and hand the caller a
    // parsed JsonDocument.
    //
    // Capturing from the actual stdout sink (rather than a TestInteractionService
    // collection) means any non-JSON text emitted on stdout — status messages,
    // update notifications, error banners — fails the test at JsonDocument.Parse.
    // This matches the pattern used by every other `--format json` test in the
    // CLI (see e.g. LsCommandTests.LsCommand_JsonFormat_ReturnsCandidateAppHosts)
    // and is what guarantees `aspire doctor --format json` stdout stays
    // machine-readable.
    //
    // The caller owns disposal of the returned JsonDocument so it can read
    // elements off it across multiple assertions in the test body.
    private async Task<JsonDocument> RunDoctorJsonAsync(
        TemporaryWorkspace workspace,
        Action<CliServiceCollectionTestOptions> configureOptions,
        Action<IServiceCollection>? configureServices = null,
        string commandLine = "doctor --format json")
    {
        var outputWriter = new TestOutputTextWriter(outputHelper);
        var services = CreateDoctorVersionServiceCollection(workspace, outputHelper, options =>
        {
            options.OutputTextWriter = outputWriter;
            configureOptions(options);
        });
        configureServices?.Invoke(services);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>();
        var result = command.Parse(commandLine);
        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(CliExitCodes.Success, exitCode);

        var stdoutText = string.Concat(outputWriter.Logs);
        return JsonDocument.Parse(stdoutText);
    }

    private static JsonElement GetCheckByName(JsonDocument document, string checkName)
        => document.RootElement.GetProperty("checks").EnumerateArray()
            .Single(check => check.GetProperty("name").GetString() == checkName);

    private static IServiceCollection CreateDoctorVersionServiceCollection(
        TemporaryWorkspace workspace,
        ITestOutputHelper outputHelper,
        Action<CliServiceCollectionTestOptions>? configure)
    {
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, configure);
        services.RemoveAll<IEnvironmentCheck>();
        services.AddSingleton<IEnvironmentCheck, AspireVersionCheck>();
        services.AddSingleton<IEnvironmentCheck, OperatingSystemCheck>();
        return services;
    }

    private static FileInfo CreateDeepAppHostFile(TemporaryWorkspace workspace, int depth)
    {
        var directory = workspace.WorkspaceRoot;
        for (var i = 0; i < depth; i++)
        {
            directory = directory.CreateSubdirectory($"level{i}");
        }

        return new FileInfo(Path.Combine(directory.FullName, "AppHost.csproj"));
    }

    private static bool TryGetOsReleaseValue(Dictionary<string, string> osRelease, string key, out string value)
    {
        if (osRelease.TryGetValue(key, out var rawValue) && !string.IsNullOrWhiteSpace(rawValue))
        {
            value = rawValue;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static string GetExpectedLinuxDisplayName(string osReleaseName)
    {
        var name = osReleaseName.Trim();
        foreach (var suffix in new[] { " GNU/Linux", " Linux" })
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^suffix.Length];
                break;
            }
        }

        return name.Equals("Linux", StringComparison.OrdinalIgnoreCase) ? "Linux" : $"Linux {name}";
    }
}
