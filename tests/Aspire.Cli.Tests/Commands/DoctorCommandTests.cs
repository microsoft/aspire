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
    public async Task DoctorCommand_Help_Works()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
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
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var doc = await RunDoctorJsonAsync(workspace,
            configureOptions: options =>
            {
                options.CliUpdateNotifierFactory = _ => new TestCliUpdateNotifier
                {
                    GetVersionStatusAsyncCallback = (_, _) => Task.FromResult(new CliVersionStatus("13.0.0", "13.1.0", "aspire update"))
                };
            });

        var cliVersionCheck = GetCheckByName(doc, "cli-version");
        Assert.Equal("aspire", cliVersionCheck.GetProperty("category").GetString());
        Assert.Equal("warning", cliVersionCheck.GetProperty("status").GetString());
        Assert.Contains("13.0.0", cliVersionCheck.GetProperty("message").GetString()!);
        Assert.Contains("13.1.0", cliVersionCheck.GetProperty("message").GetString()!);
        var cliVersionMetadata = cliVersionCheck.GetProperty("metadata");
        Assert.Equal("13.0.0", cliVersionMetadata.GetProperty("currentVersion").GetString());
        Assert.Equal("13.1.0", cliVersionMetadata.GetProperty("latestVersion").GetString());
        Assert.Equal("aspire update", cliVersionMetadata.GetProperty("updateCommand").GetString());
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
        using var workspace = TemporaryWorkspace.Create(outputHelper);
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
        using var workspace = TemporaryWorkspace.Create(outputHelper);
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

        var appHostVersionCheck = GetCheckByName(doc, "apphost-version");
        Assert.Equal("apphost", appHostVersionCheck.GetProperty("category").GetString());
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
        using var workspace = TemporaryWorkspace.Create(outputHelper);
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

        var appHostVersionCheck = GetCheckByName(doc, "apphost-version");
        Assert.Equal("apphost", appHostVersionCheck.GetProperty("category").GetString());
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
        using var workspace = TemporaryWorkspace.Create(outputHelper);
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
            check => check.GetProperty("name").GetString() == "apphost-version");
    }

    [Fact]
    public async Task DoctorCommand_Json_DoesNotShowAppHostVersionForNonAppHostProject()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
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
            check => check.GetProperty("name").GetString() == "apphost-version");
    }

    [Fact]
    public async Task DoctorCommand_Json_DoesNotDiscoverNestedAppHostWhenAnotherProjectExists()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
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
            check => check.GetProperty("name").GetString() == "apphost-version");
    }

    [Fact]
    public async Task DoctorCommand_Json_DoesNotChooseBetweenMultipleDirectAppHostsWithoutConfig()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
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
            check => check.GetProperty("name").GetString() == "apphost-version");
    }

    [Fact]
    public async Task DoctorCommand_Json_PreservesCliVersionWhenAppHostVersionResolutionFails()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
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

        var cliVersionCheck = GetCheckByName(doc, "cli-version");
        var appHostVersionCheck = GetCheckByName(doc, "apphost-version");

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
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        using var doc = await RunDoctorJsonAsync(workspace,
            configureOptions: options =>
            {
                options.CliUpdateNotifierFactory = _ => new TestCliUpdateNotifier();
                options.ProjectLocatorFactory = _ => new TestProjectLocator
                {
                    GetAppHostFromSettingsAsyncCallback = _ => throw new IOException("settings lookup failed")
                };
            });

        var cliVersionCheck = GetCheckByName(doc, "cli-version");
        var appHostVersionCheck = GetCheckByName(doc, "apphost-version");

        Assert.Equal("pass", cliVersionCheck.GetProperty("status").GetString());
        Assert.Equal("warning", appHostVersionCheck.GetProperty("status").GetString());
        Assert.Equal("settings lookup failed", appHostVersionCheck.GetProperty("details").GetString());
    }

    [Fact]
    public async Task DoctorCommand_Json_UsesConfiguredAppHostBeyondLanguageDetectionLimit()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
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

        var appHostVersionCheck = GetCheckByName(doc, "apphost-version");
        Assert.Equal("apphost", appHostVersionCheck.GetProperty("category").GetString());
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
        using var workspace = TemporaryWorkspace.Create(outputHelper);
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

        var cliVersionCheck = GetCheckByName(doc, "cli-version");
        var metadata = cliVersionCheck.GetProperty("metadata");
        Assert.Equal("staging", metadata.GetProperty("identityChannel").GetString());
        Assert.Contains("channel: staging", cliVersionCheck.GetProperty("message").GetString()!);
    }

    [Fact]
    public async Task DoctorCommand_Json_CliVersion_OmitsIdentityChannelWhenReaderThrows()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
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
        var cliVersionCheck = GetCheckByName(doc, "cli-version");
        var metadata = cliVersionCheck.GetProperty("metadata");
        Assert.False(metadata.TryGetProperty("identityChannel", out _));
        Assert.DoesNotContain("channel:", cliVersionCheck.GetProperty("message").GetString()!);
    }

    [Fact]
    public async Task DoctorCommand_Json_AppHostVersion_IncludesPinnedChannelFromAspireConfig()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
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

        var appHostVersionCheck = GetCheckByName(doc, "apphost-version");
        var metadata = appHostVersionCheck.GetProperty("metadata");
        Assert.Equal("daily", metadata.GetProperty("pinnedChannel").GetString());
        Assert.Contains("channel: daily", appHostVersionCheck.GetProperty("message").GetString()!);
    }

    [Fact]
    public async Task DoctorCommand_Json_AppHostVersion_IncludesPinnedChannelFromAspireConfigWhenAppHostIsNested()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
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

        var appHostVersionCheck = GetCheckByName(doc, "apphost-version");
        var metadata = appHostVersionCheck.GetProperty("metadata");
        Assert.Equal("daily", metadata.GetProperty("pinnedChannel").GetString());
        Assert.Contains("channel: daily", appHostVersionCheck.GetProperty("message").GetString()!);
        Assert.Equal(Path.Combine("src", "NestedAppHost", "AppHost.csproj"), metadata.GetProperty("appHostPath").GetString());
    }

    [Fact]
    public async Task DoctorCommand_Json_AppHostVersion_OmitsPinnedChannelWhenAspireConfigAbsent()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
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

        var appHostVersionCheck = GetCheckByName(doc, "apphost-version");
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
        using var workspace = TemporaryWorkspace.Create(outputHelper);
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

        var cliVersionCheck = GetCheckByName(doc, "cli-version");

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
    public async Task DoctorCommand_Json_IncludesDiscoveredInstallations()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var doc = await RunDoctorJsonAsync(workspace,
            configureOptions: options =>
            {
                options.CliUpdateNotifierFactory = _ => new TestCliUpdateNotifier();
            },
            configureServices: services => UseFakeInstallationDiscovery(
                services,
                self: new InstallationInfo
                {
                    Path = "/home/test/.aspire/bin/aspire",
                    CanonicalPath = "/home/test/.aspire/bin/aspire",
                    Version = "13.0.0",
                    Channel = "stable",
                    Route = "script",
                    PathStatus = InstallationPathStatus.Active,
                    Status = InstallationInfoStatus.Ok,
                },
                others:
                [
                    new InstallationInfo
                    {
                        Path = "/home/test/.aspire/dogfood/pr-1234/bin/aspire",
                        CanonicalPath = "/home/test/.aspire/dogfood/pr-1234/bin/aspire",
                        Version = "13.1.0-preview",
                        Channel = "pr-1234",
                        Route = "pr",
                        PathStatus = InstallationPathStatus.Shadowed,
                        Status = InstallationInfoStatus.Ok,
                    },
                ]));

        var installations = doc.RootElement.GetProperty("installations").EnumerateArray().ToArray();
        Assert.Equal(2, installations.Length);

        var self = installations[0];
        Assert.Equal("/home/test/.aspire/bin/aspire", self.GetProperty("path").GetString());
        Assert.Equal("stable", self.GetProperty("channel").GetString());
        Assert.Equal("script", self.GetProperty("route").GetString());
        Assert.Equal(InstallationPathStatus.Active, self.GetProperty("pathStatus").GetString());

        var peer = installations[1];
        Assert.Equal("/home/test/.aspire/dogfood/pr-1234/bin/aspire", peer.GetProperty("path").GetString());
        Assert.Equal("pr-1234", peer.GetProperty("channel").GetString());
        Assert.Equal("pr", peer.GetProperty("route").GetString());
        Assert.Equal(InstallationPathStatus.Shadowed, peer.GetProperty("pathStatus").GetString());
    }

    [Fact]
    public async Task DoctorCommand_HumanReadable_Self_RendersOnlyRunningInstallationAndSkipsChecks()
    {
        // `doctor --self` is the peer-probe surface. Without --format the
        // human-readable table is the default; with --format json the
        // probe gets a machine-readable row. Either way, no environment
        // checks run and only the running CLI's row is rendered.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
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
        UseFakeInstallationDiscovery(
            services,
            self: new InstallationInfo
            {
                Path = "/home/test/.aspire/bin/aspire",
                CanonicalPath = "/home/test/.aspire/bin/aspire",
                Version = "13.4.0-pr.17115.gcd700928",
                Channel = "pr-17115",
                Route = "brew",
                PathStatus = InstallationPathStatus.Active,
                Status = InstallationInfoStatus.Ok,
            },
            others:
            [
                new InstallationInfo
                {
                    Path = "/peer/aspire",
                    CanonicalPath = "/peer/aspire",
                    Version = "13.1.0-preview",
                    Channel = "pr-1234",
                    Route = "pr",
                    PathStatus = InstallationPathStatus.Shadowed,
                    Status = InstallationInfoStatus.Ok,
                },
            ]);

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>();
        var result = command.Parse("doctor --self");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        var rendered = output.ToString();
        Assert.Contains("Aspire CLI Installations", rendered, StringComparison.Ordinal);
        Assert.Contains("13.4.0-pr.17115.gcd700928", rendered, StringComparison.Ordinal);
        Assert.Contains("pr-17115", rendered, StringComparison.Ordinal);
        // No environment checks ran, so no Summary line.
        Assert.DoesNotContain("Summary:", rendered, StringComparison.Ordinal);
        // No peer rows — --self bounds the output to the running CLI only.
        Assert.DoesNotContain("/peer/aspire", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("pr-1234", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DoctorCommand_HumanReadable_AppendsInstallationsAfterSummary()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
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
        UseFakeInstallationDiscovery(
            services,
            self: new InstallationInfo
            {
                Path = "/home/test/.aspire/bin/aspire",
                CanonicalPath = "/home/test/.aspire/bin/aspire",
                Version = "13.0.0",
                Channel = "stable",
                Route = "script",
                PathStatus = InstallationPathStatus.Active,
                Status = InstallationInfoStatus.Ok,
            },
            others:
            [
                new InstallationInfo
                {
                    Path = "/peer/aspire",
                    CanonicalPath = "/peer/aspire",
                    Version = "13.1.0-preview",
                    Channel = "pr-1234",
                    Route = "pr",
                    PathStatus = InstallationPathStatus.Shadowed,
                    Status = InstallationInfoStatus.Ok,
                },
            ]);

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>();
        var result = command.Parse("doctor");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        var rendered = output.ToString();
        var summaryIndex = rendered.IndexOf("Summary:", StringComparison.Ordinal);
        var installationsIndex = rendered.IndexOf("Aspire CLI Installations", StringComparison.Ordinal);
        Assert.True(summaryIndex >= 0, $"Expected doctor summary in output:{Environment.NewLine}{rendered}");
        Assert.True(installationsIndex > summaryIndex, $"Expected installations after summary in output:{Environment.NewLine}{rendered}");
        Assert.Contains("/peer/aspire", rendered, StringComparison.Ordinal);
        Assert.Contains("pr-1234", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DoctorCommand_HumanReadable_EscapesUnknownPathStatus()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
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

        // pathStatus is parsed from untrusted peer-probe stdout. A peer
        // that emits an unrecognized string must not be able to inject
        // Spectre markup into the parent's rendered table; the default
        // branch of PathStatusDisplay must EscapeMarkup() the value.
        var services = CreateDoctorVersionServiceCollection(workspace, outputHelper, options =>
        {
            options.CliUpdateNotifierFactory = _ => new TestCliUpdateNotifier();
        });
        services.RemoveAll<IAnsiConsole>();
        services.AddSingleton<IAnsiConsole>(console);
        UseFakeInstallationDiscovery(
            services,
            self: new InstallationInfo
            {
                Path = "/home/test/.aspire/bin/aspire",
                CanonicalPath = "/home/test/.aspire/bin/aspire",
                Version = "13.0.0",
                Channel = "stable",
                Route = "script",
                PathStatus = "custom[red]status[/]",
                Status = InstallationInfoStatus.Ok,
            });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>();
        var result = command.Parse("doctor");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        var rendered = output.ToString();
        Assert.Contains("custom[red]status[/]", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void DoctorCommand_InfoCommandIsNotRegistered()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>();

        Assert.DoesNotContain(command.Subcommands, subcommand => subcommand.Name == "info");
    }

    [Fact]
    public async Task DoctorCommand_Json_Self_ReturnsOnlyRunningInstallation()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var doc = await RunDoctorJsonAsync(workspace,
            commandLine: "doctor --self --format json",
            configureOptions: options =>
            {
                options.CliUpdateNotifierFactory = _ => new TestCliUpdateNotifier();
            },
            configureServices: services => UseFakeInstallationDiscovery(
                services,
                self: new InstallationInfo
                {
                    Path = "/usr/local/bin/aspire",
                    CanonicalPath = "/usr/local/bin/aspire",
                    Version = "13.0.0",
                    Channel = "stable",
                    Route = "script",
                    PathStatus = InstallationPathStatus.Active,
                    Status = InstallationInfoStatus.Ok,
                }));

        Assert.Empty(doc.RootElement.GetProperty("checks").EnumerateArray());
        var installations = doc.RootElement.GetProperty("installations").EnumerateArray().ToArray();
        var row = Assert.Single(installations);
        Assert.Equal("/usr/local/bin/aspire", row.GetProperty("path").GetString());
        Assert.Equal("13.0.0", row.GetProperty("version").GetString());
        Assert.Equal("stable", row.GetProperty("channel").GetString());
        Assert.Equal("script", row.GetProperty("route").GetString());
        Assert.Equal(InstallationPathStatus.Active, row.GetProperty("pathStatus").GetString());
        Assert.Equal(InstallationInfoStatus.Ok, row.GetProperty("status").GetString());
    }

    [Fact]
    public async Task DoctorCommand_Json_WhenInstallDiscoveryFails_StillReturnsDoctorResults()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var doc = await RunDoctorJsonAsync(workspace,
            configureOptions: options =>
            {
                options.CliUpdateNotifierFactory = _ => new TestCliUpdateNotifier();
            },
            configureServices: services => UseFakeInstallationDiscovery(
                services,
                self: new InstallationInfo
                {
                    Path = "/test/aspire",
                    Status = InstallationInfoStatus.Ok,
                },
                discoverAllException: new IOException("PATH lookup failed")));

        Assert.NotEmpty(doc.RootElement.GetProperty("checks").EnumerateArray());
        var row = Assert.Single(doc.RootElement.GetProperty("installations").EnumerateArray());
        Assert.Equal(InstallationInfoStatus.Failed, row.GetProperty("status").GetString());
        Assert.Equal("Install discovery failed. See the Aspire CLI logs for details.", row.GetProperty("statusReason").GetString());
    }

    [Theory]
    [InlineData(InstallationInfoStatus.Failed, "(probe failed)")]
    [InlineData(InstallationInfoStatus.NotProbed, "(not probed)")]
    [InlineData(InstallationInfoStatus.Ok, "(unknown)")]
    public async Task DoctorCommand_HumanReadable_RendersMissingInstallationValuesBasedOnStatus(string status, string expectedPlaceholder)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
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
        UseFakeInstallationDiscovery(
            services,
            self: new InstallationInfo
            {
                Path = "/home/test/.aspire/bin/aspire",
                CanonicalPath = "/home/test/.aspire/bin/aspire",
                Version = "13.0.0",
                Channel = "stable",
                Route = "script",
                PathStatus = InstallationPathStatus.Active,
                Status = InstallationInfoStatus.Ok,
            },
            others:
            [
                new InstallationInfo
                {
                    Path = $"/peer/{status}/aspire",
                    CanonicalPath = $"/peer/{status}/aspire",
                    Status = status,
                },
            ]);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>();
        var result = command.Parse("doctor");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(CliExitCodes.Success, exitCode);

        var rendered = output.ToString();
        Assert.Contains($"/peer/{status}/aspire", rendered, StringComparison.Ordinal);
        Assert.Contains(expectedPlaceholder, rendered, StringComparison.Ordinal);

        foreach (var otherPlaceholder in new[] { "(probe failed)", "(not probed)", "(unknown)" }.Where(p => p != expectedPlaceholder))
        {
            Assert.DoesNotContain(otherPlaceholder, rendered, StringComparison.Ordinal);
        }
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
        UseFakeInstallationDiscovery(
            services,
            self: new InstallationInfo
            {
                Path = "/test/aspire",
                CanonicalPath = "/test/aspire",
                Version = "13.0.0",
                Channel = "stable",
                Route = "script",
                PathStatus = InstallationPathStatus.Active,
                Status = InstallationInfoStatus.Ok,
            });
        return services;
    }

    private static void UseFakeInstallationDiscovery(
        IServiceCollection services,
        InstallationInfo self,
        IReadOnlyList<InstallationInfo>? others = null,
        Exception? discoverAllException = null)
    {
        services.RemoveAll<IInstallationDiscovery>();
        services.AddSingleton<IInstallationDiscovery>(_ => new FakeInstallationDiscovery(self, others, discoverAllException));
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
}
