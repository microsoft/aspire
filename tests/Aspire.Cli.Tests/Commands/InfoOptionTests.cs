// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Acquisition;
using Aspire.Cli.Commands;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using System.Text.Json.Nodes;

namespace Aspire.Cli.Tests.Commands;

public class InfoOptionTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void Info_IsInformationalOption_SoFirstRunNoticeAndTelemetryAreSuppressed()
    {
        // Wired via CommonOptionNames so DisplayFirstTimeUseNoticeIfNeededAsync
        // does not print the banner or consume the one-shot sentinel and the
        // telemetry manager opts out for `aspire --info` (the text form is the
        // path not covered by HasMachineReadableOutputFormat).
        Assert.Contains(CommonOptionNames.Info, CommonOptionNames.InformationalOptionNames);
    }

    [Fact]
    public async Task Info_Self_Json_ReturnsOnlyRunningInstallationAsArray()
    {
        // Cross-version peer-probe contract: PeerInstallProbe.TryParseRichProbeResult
        // expects a bare array of InstallationInfo, not the wrapped object form.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outputWriter = new TestOutputTextWriter(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.OutputTextWriter = outputWriter;
            options.DisableAnsi = true;
        });
        services.Replace(ServiceDescriptor.Singleton<IInstallationDiscovery>(_ => new FakeInstallationDiscovery(
            new InstallationInfo
            {
                Path = "/usr/local/bin/aspire",
                CanonicalPath = "/usr/local/bin/aspire",
                Version = "13.0.0",
                Channel = "stable",
                Source = "script",
                PathStatus = InstallationPathStatus.Active,
                Status = InstallationInfoStatus.Ok,
            },
            [
                new InstallationInfo
                {
                    Path = "/peer/aspire",
                    CanonicalPath = "/peer/aspire",
                    Version = "13.1.0-preview",
                    Channel = "pr-1234",
                    Source = "pr",
                    PathStatus = InstallationPathStatus.Shadowed,
                    Status = InstallationInfoStatus.Ok,
                }
            ])));
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("--info --self --format json");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        var installations = JsonNode.Parse(string.Join(Environment.NewLine, outputWriter.Logs))!.AsArray();
        var row = Assert.Single(installations);
        Assert.Equal("/usr/local/bin/aspire", row!["path"]!.GetValue<string>());
        Assert.Equal("13.0.0", row["version"]!.GetValue<string>());
        Assert.Equal("stable", row["channel"]!.GetValue<string>());
        Assert.Equal("script", row["source"]!.GetValue<string>());
        Assert.Equal(InstallationPathStatus.Active, row["pathStatus"]!.GetValue<string>());
        Assert.Equal(InstallationInfoStatus.Ok, row["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task Info_Self_Text_ReturnsOnlyRunningInstallationWithoutChecks()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outputWriter = new TestOutputTextWriter(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.OutputTextWriter = outputWriter;
            options.DisableAnsi = true;
        });
        services.Replace(ServiceDescriptor.Singleton<IInstallationDiscovery>(_ => new FakeInstallationDiscovery(
            new InstallationInfo
            {
                Path = "/usr/local/bin/aspire",
                Version = "13.0.0",
                Channel = "stable",
                Source = "script",
                PathStatus = "custom[red]status[/]",
                Status = InstallationInfoStatus.Ok,
            },
            [
                new InstallationInfo
                {
                    Path = "/peer/aspire",
                    Status = InstallationInfoStatus.Ok,
                }
            ])));
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("--info --self");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        var output = string.Join(Environment.NewLine, outputWriter.Logs);
        Assert.Contains("/usr/local/bin/aspire", output, StringComparison.Ordinal);
        Assert.Contains("custom[red]status[/]", output, StringComparison.Ordinal);
        Assert.DoesNotContain("/peer/aspire", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Info_Text_ShowsDiscoveredInstallsAndOrphanHives()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var aspireHome = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire");
        Directory.CreateDirectory(Path.Combine(aspireHome, "hives", "pr-17400", "packages"));

        var outputWriter = new TestOutputTextWriter(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.OutputTextWriter = outputWriter;
            options.DisableAnsi = true;
        });
        services.Replace(ServiceDescriptor.Singleton<IInstallationDiscovery>(_ => new FakeInstallationDiscovery(
            new InstallationInfo
            {
                Path = Path.Combine(aspireHome, "bin", "aspire"),
                CanonicalPath = Path.Combine(aspireHome, "bin", "aspire"),
                Version = "13.2.0",
                Channel = "stable",
                Source = "script",
                PathStatus = InstallationPathStatus.Active,
                Status = InstallationInfoStatus.Ok
            })));
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("--info");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        var output = string.Join(Environment.NewLine, outputWriter.Logs);
        Assert.Contains("script", output);
        Assert.Contains("stable", output);
        Assert.Contains("orphan-hive", output);
        Assert.Contains("pr-17400", output);
        Assert.Contains("no install found", output);
        Assert.DoesNotContain("│", output);
    }

    [Fact]
    public async Task Info_FormatList_IsAcceptedAndUsesVerticalOutput()
    {
        // `--format list` is the explicit spelling of the default text rendering.
        // Carried from the previous installs surface so existing automation that
        // sets `--format list` doesn't break, and to anchor the enum so arbitrary
        // values like `table` are rejected by the System.CommandLine value parser.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var aspireHome = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire");

        var outputWriter = new TestOutputTextWriter(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.OutputTextWriter = outputWriter;
            options.DisableAnsi = true;
        });
        services.Replace(ServiceDescriptor.Singleton<IInstallationDiscovery>(_ => new FakeInstallationDiscovery(
            new InstallationInfo
            {
                Path = Path.Combine(aspireHome, "bin", "aspire"),
                CanonicalPath = Path.Combine(aspireHome, "bin", "aspire"),
                Version = "13.2.0",
                Channel = "stable",
                Source = "script",
                PathStatus = InstallationPathStatus.Active,
                Status = InstallationInfoStatus.Ok
            })));
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("--info --format list");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        var output = string.Join(Environment.NewLine, outputWriter.Logs);
        Assert.DoesNotContain("│", output);
    }

    [Fact]
    public void Info_FormatTable_IsRejected()
    {
        // Anchor the enum: `table` is not a valid value, so the parser must
        // reject it rather than silently fall through.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("--info --format table");

        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public async Task Info_FailedRow_DoesNotDuplicateReasonAcrossStatusAndReasonFields()
    {
        // The structured `status` field must stay enum-shaped ("failed",
        // "notProbed", "active", ...) — the human-readable message rides on
        // `statusReason` alone. Concatenating the two would (a) force JSON
        // consumers to split on ": " before they can `switch` on status and
        // (b) make the human surface print the reason twice (once in the
        // Status line, once in the Reason line). Guarded for both the JSON
        // and the human surfaces.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outputWriter = new TestOutputTextWriter(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.OutputTextWriter = outputWriter;
            options.DisableAnsi = true;
        });
        services.Replace(ServiceDescriptor.Singleton<IInstallationDiscovery>(_ => new FakeInstallationDiscovery(
            new InstallationInfo
            {
                Path = "/peer/aspire",
                CanonicalPath = "/peer/aspire",
                Channel = "stable",
                Source = "script",
                PathStatus = InstallationPathStatus.Shadowed,
                Status = InstallationInfoStatus.Failed,
                StatusReason = "timed out after 5s"
            })));
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();

        var listExitCode = await command.Parse("--info").InvokeAsync().DefaultTimeout();
        Assert.Equal(CliExitCodes.Success, listExitCode);
        var listOutput = string.Join(Environment.NewLine, outputWriter.Logs);
        Assert.Contains("Status   failed", listOutput);
        Assert.DoesNotContain("Status   failed:", listOutput);
        Assert.Contains("Reason   timed out after 5s", listOutput);

        outputWriter.Logs.Clear();

        var jsonExitCode = await command.Parse("--info --format json").InvokeAsync().DefaultTimeout();
        Assert.Equal(CliExitCodes.Success, jsonExitCode);
        var payload = JsonNode.Parse(string.Join(Environment.NewLine, outputWriter.Logs))!.AsObject();
        var row = payload["installs"]!.AsArray()[0]!;
        Assert.Equal("failed", row["status"]!.GetValue<string>());
        Assert.Equal("timed out after 5s", row["statusReason"]!.GetValue<string>());
    }

    [Fact]
    public async Task Info_Json_IncludesDiscoveredInstallsBrokenRowsAndOrphanHives()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var aspireHome = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire");
        Directory.CreateDirectory(Path.Combine(aspireHome, "hives", "pr-17400", "packages"));

        var outputWriter = new TestOutputTextWriter(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.OutputTextWriter = outputWriter;
            options.DisableAnsi = true;
        });
        services.Replace(ServiceDescriptor.Singleton<IInstallationDiscovery>(_ => new FakeInstallationDiscovery(
            new InstallationInfo
            {
                Path = Path.Combine(aspireHome, "bin", "aspire"),
                CanonicalPath = Path.Combine(aspireHome, "bin", "aspire"),
                Version = "13.2.0",
                Channel = "stable",
                Source = "script",
                PathStatus = InstallationPathStatus.Active,
                Status = InstallationInfoStatus.Ok
            },
            [
                new InstallationInfo
                {
                    Path = Path.Combine(aspireHome, "broken", "aspire"),
                    CanonicalPath = Path.Combine(aspireHome, "broken", "aspire"),
                    Channel = "daily",
                    Source = "script",
                    PathStatus = InstallationPathStatus.Shadowed,
                    Status = InstallationInfoStatus.Failed,
                    StatusReason = "peer probe failed"
                }
            ])));
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("--info --format json");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        var payload = JsonNode.Parse(string.Join(Environment.NewLine, outputWriter.Logs))!.AsObject();
        Assert.NotNull(payload["version"]);
        Assert.Equal("13.2.0", payload["version"]!.GetValue<string>());
        var installs = payload["installs"]!.AsArray();
        Assert.Collection(
            installs,
            row =>
            {
                Assert.Equal("script", row!["id"]!.GetValue<string>());
                Assert.Equal("script", row["kind"]!.GetValue<string>());
                Assert.Equal("stable", row["channel"]!.GetValue<string>());
                Assert.Equal("active", row["status"]!.GetValue<string>());
            },
            row =>
            {
                Assert.Equal("script-2", row!["id"]!.GetValue<string>());
                Assert.Equal("failed", row["status"]!.GetValue<string>());
                Assert.Equal("peer probe failed", row["statusReason"]!.GetValue<string>());
            },
            row =>
            {
                Assert.Equal("pr-17400", row!["id"]!.GetValue<string>());
                Assert.Equal("orphan-hive", row["kind"]!.GetValue<string>());
                Assert.Equal("no install found", row["status"]!.GetValue<string>());
            });
    }

    [Fact]
    public async Task Info_DiscoveryWalkFails_StillExitsZero_AndEmitsFailureRow()
    {
        // Matches the tolerant posture of the channel-read and self-version
        // paths: `aspire --info` is the surface a user reaches for to
        // diagnose a flaky environment, so a discovery walk that throws
        // (filesystem ACL, IO error on ~/.aspire/hives, ...) must not crash
        // the command. Exit 0, surface a single failure row with the
        // underlying reason in statusReason, keep channel/version intact.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outputWriter = new TestOutputTextWriter(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.OutputTextWriter = outputWriter;
            options.DisableAnsi = true;
        });
        services.Replace(ServiceDescriptor.Singleton<IInstallationDiscovery>(_ => new FakeInstallationDiscovery(
            new InstallationInfo
            {
                Path = "/usr/local/bin/aspire",
                Channel = "stable",
                Status = InstallationInfoStatus.Ok,
            },
            discoverAllException: new IOException("PATH lookup failed"))));
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("--info --format json");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        var payload = JsonNode.Parse(string.Join(Environment.NewLine, outputWriter.Logs))!.AsObject();
        var installs = payload["installs"]!.AsArray();
        var row = Assert.Single(installs);
        Assert.Equal("discovery", row!["id"]!.GetValue<string>());
        Assert.Equal("discovery-failed", row["kind"]!.GetValue<string>());
        Assert.Equal(InstallationInfoStatus.Failed, row["status"]!.GetValue<string>());
        Assert.Equal("PATH lookup failed", row["statusReason"]!.GetValue<string>());
    }

    [Fact]
    public async Task Info_Json_EmptyInstalls_StillEmitsArray()
    {
        // When discovery returns nothing, the installs field must still appear
        // (as an empty array) so consumers don't need a "missing or empty"
        // branch.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outputWriter = new TestOutputTextWriter(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.OutputTextWriter = outputWriter;
            options.DisableAnsi = true;
        });
        services.Replace(ServiceDescriptor.Singleton<IInstallationDiscovery>(_ => new FakeInstallationDiscovery(
            new InstallationInfo
            {
                // The self row carries a non-aspire filename and no source, so
                // IsDisplayableInstall filters it out.
                Path = "/opt/homebrew/Cellar/dotnet/10.0.0/libexec/dotnet",
                CanonicalPath = "/opt/homebrew/Cellar/dotnet/10.0.0/libexec/dotnet",
                Channel = "local",
                Source = null,
                PathStatus = InstallationPathStatus.NotOnPath,
                Status = InstallationInfoStatus.Ok,
            })));
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("--info --format json");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        var payload = JsonNode.Parse(string.Join(Environment.NewLine, outputWriter.Logs))!.AsObject();
        var installs = payload["installs"]!.AsArray();
        Assert.Empty(installs);
    }

    [Fact]
    public async Task Info_ChannelLocal_IsNotTreatedAsFailure()
    {
        // "local" is the developer-build default. It must serialize as
        // "channel": "local", not be dropped or mapped to null.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outputWriter = new TestOutputTextWriter(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.OutputTextWriter = outputWriter;
            options.DisableAnsi = true;
        });
        services.Replace(ServiceDescriptor.Singleton<IIdentityChannelReader>(_ => new ConstantChannelReader("local")));
        services.Replace(ServiceDescriptor.Singleton<IInstallationDiscovery>(_ => new FakeInstallationDiscovery(
            new InstallationInfo
            {
                Path = "/local/aspire",
                CanonicalPath = "/local/aspire",
                Version = "13.2.0",
                Channel = "local",
                Source = "localhive",
                PathStatus = InstallationPathStatus.Active,
                Status = InstallationInfoStatus.Ok,
            })));
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("--info --format json");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        var payload = JsonNode.Parse(string.Join(Environment.NewLine, outputWriter.Logs))!.AsObject();
        Assert.Equal("local", payload["channel"]!.GetValue<string>());
    }

    [Fact]
    public async Task Info_ChannelReadFailure_ExitsZero_AndOmitsChannel()
    {
        // A binary with missing/invalid AspireCliChannel assembly metadata must
        // not take `aspire --info` down with it — that's the surface a user
        // reaches for to *find out* their binary is broken. The channel field
        // is omitted from JSON (via DefaultIgnoreCondition.WhenWritingNull)
        // and the text row is skipped, but the command still exits 0.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outputWriter = new TestOutputTextWriter(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.OutputTextWriter = outputWriter;
            options.DisableAnsi = true;
        });
        services.Replace(ServiceDescriptor.Singleton<IIdentityChannelReader>(_ => new ThrowingChannelReader()));
        services.Replace(ServiceDescriptor.Singleton<IInstallationDiscovery>(_ => new FakeInstallationDiscovery(
            new InstallationInfo
            {
                Path = "/broken/aspire",
                CanonicalPath = "/broken/aspire",
                Version = "13.2.0",
                Channel = null,
                Source = "script",
                PathStatus = InstallationPathStatus.NotOnPath,
                Status = InstallationInfoStatus.Ok,
            })));
        using var provider = services.BuildServiceProvider();

        // CliExecutionContext must still resolve even though the channel
        // reader throws — the production DI factory defers the read via
        // Lazy<string> and InfoOptionAction injects IIdentityChannelReader
        // directly with its own try/catch. Locks the lazy-channel refactor in.
        _ = provider.GetRequiredService<CliExecutionContext>();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("--info --format json");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        var payload = JsonNode.Parse(string.Join(Environment.NewLine, outputWriter.Logs))!.AsObject();
        Assert.False(payload.ContainsKey("channel"));
    }

    [Fact]
    public async Task Info_ChannelReadFailure_Text_OmitsChannelLine()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outputWriter = new TestOutputTextWriter(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.OutputTextWriter = outputWriter;
            options.DisableAnsi = true;
        });
        services.Replace(ServiceDescriptor.Singleton<IIdentityChannelReader>(_ => new ThrowingChannelReader()));
        services.Replace(ServiceDescriptor.Singleton<IInstallationDiscovery>(_ => new FakeInstallationDiscovery(
            new InstallationInfo
            {
                Path = "/broken/aspire",
                CanonicalPath = "/broken/aspire",
                Version = "13.2.0",
                Channel = null,
                Source = "script",
                PathStatus = InstallationPathStatus.NotOnPath,
                Status = InstallationInfoStatus.Ok,
            })));
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("--info");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        var output = string.Join(Environment.NewLine, outputWriter.Logs);
        Assert.Contains("Version  13.2.0", output);
        Assert.DoesNotContain("Channel  ", output);
    }

    [Fact]
    public async Task Info_LogsClassificationReasons()
    {
        // Operational logs from the row-builder are operationally important
        // for diagnosing why a peer is missing or extra. Carried from the
        // previous installs surface.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var aspireHome = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire");
        Directory.CreateDirectory(Path.Combine(aspireHome, "hives", "pr-17400", "packages"));
        var logger = new CapturingLogger<InfoOptionAction>();

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        services.Replace(ServiceDescriptor.Singleton<ILogger<InfoOptionAction>>(logger));
        services.Replace(ServiceDescriptor.Singleton<IInstallationDiscovery>(_ => new FakeInstallationDiscovery(
            new InstallationInfo
            {
                Path = "/opt/homebrew/Cellar/dotnet/10.0.107/libexec/dotnet",
                CanonicalPath = "/opt/homebrew/Cellar/dotnet/10.0.107/libexec/dotnet",
                Version = "13.2.0",
                Channel = "local",
                Source = null,
                PathStatus = InstallationPathStatus.NotOnPath,
                Status = InstallationInfoStatus.Ok
            },
            [
                new InstallationInfo
                {
                    Path = Path.Combine(aspireHome, "bin", "aspire"),
                    CanonicalPath = Path.Combine(aspireHome, "bin", "aspire"),
                    Version = "13.2.0",
                    Channel = "local",
                    Source = "localhive",
                    PathStatus = InstallationPathStatus.Active,
                    Status = InstallationInfoStatus.Ok
                }
            ])));
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("--info");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Debug && e.Message.Contains("Ignoring discovery row path", StringComparison.Ordinal));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Debug && e.Message.Contains("Classified install path", StringComparison.Ordinal) && e.Message.Contains("localhive", StringComparison.Ordinal));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Debug && e.Message.Contains("Classified hive", StringComparison.Ordinal) && e.Message.Contains("orphan", StringComparison.Ordinal));
    }

    [Fact]
    public void Info_SelfWithoutInfo_FailsParse()
    {
        // The --self modifier is only meaningful when --info is also set.
        // Without the validator, `aspire --self` would silently fall through
        // to grouped help with InvalidCommand and look like a no-op.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("--self");

        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Info_FormatWithoutInfo_FailsParse()
    {
        // The --format modifier is only meaningful when --info is also set.
        // Without the validator, the root-level --format could swallow a
        // value intended for a subcommand option (e.g. `--format json doctor`
        // would parse as a root-level --format=json followed by an unrelated
        // doctor subcommand).
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("--format json");

        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Doctor_FormatJson_StillParsesAsSubcommandOption()
    {
        // Regression guard: the new root-level --format must not break
        // existing `doctor --format json` invocations. Doctor declares its
        // own --format option; the validator on the root-level --format
        // gates that to require --info, so this token is consumed by the
        // doctor subcommand instead.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("doctor --format json");

        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Info_OnSubcommand_DoesNotFireInfoAction()
    {
        // `aspire run --info` is *separate from* `aspire --info`: --info is
        // root-only (not recursive), so when supplied on a subcommand it does
        // not bind to the root option, the InfoOptionAction does not fire,
        // and the subcommand's normal parse runs. This keeps install
        // enumeration a deliberate top-level operation and avoids the
        // recursive-option / subcommand-local --format shadowing trap, where
        // `aspire run --info --format json` would otherwise silently emit
        // install text because run's own --format swallows the token.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("run --info");

        // --info supplied on the subcommand does not bind to the root option.
        // GetValue returns the option default (false), proving the
        // InfoOptionAction wouldn't fire for this parse.
        Assert.False(result.GetValue(command.InfoOption));
        // The parse selects the `run` subcommand instead of the root.
        Assert.NotNull(result.CommandResult);
        Assert.NotSame(command, result.CommandResult.Command);
    }

    [Fact]
    public void Info_OnSubcommand_WithFormat_BindsFormatToSubcommand()
    {
        // Companion to Info_OnSubcommand_DoesNotFireInfoAction: with --info
        // non-recursive, the trailing `--format json` after a subcommand
        // also doesn't bind to root's InfoFormatOption, so there's no
        // root-level --format value lingering after parse. Run's own
        // --format option still owns the token; this test pins that
        // routing so a future change to InfoFormatOption recursion can't
        // silently resurrect the shadowing bug where `aspire run --info
        // --format json` looked like it should emit info JSON but actually
        // ran the subcommand with `--format=json`.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("run --info --format json");

        Assert.False(result.GetValue(command.InfoOption));
        // Root-level InfoFormatOption sees its default (List), confirming
        // the `--format json` token did not bind here.
        Assert.Equal(InfoOutputFormat.List, result.GetValue(command.InfoFormatOption));
    }

    [Fact]
    public async Task Info_Self_Json_IsParseableByInstallationInfoParser_PeerProbeContract()
    {
        // End-to-end self-loop on the cross-version peer-probe contract:
        // drive the action with `--info --self --format json`, capture
        // stdout, and feed it through InstallationInfoParser.Parse — the
        // same code path PeerInstallProbe.TryParseRichProbeResult uses to
        // shape a peer's response. Without this test, a future refactor
        // of InstallationInfoOutput.DescribeSelfSafely or the JSON source
        // generation context could silently drift the wire shape away from
        // what the parser expects, and the existing PeerInstallProbeTests
        // script fakes wouldn't catch it because they hard-code the JSON
        // shape in the script.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outputWriter = new TestOutputTextWriter(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.OutputTextWriter = outputWriter;
            options.DisableAnsi = true;
        });
        services.Replace(ServiceDescriptor.Singleton<IInstallationDiscovery>(_ => new FakeInstallationDiscovery(
            new InstallationInfo
            {
                Path = "/peer/aspire",
                CanonicalPath = "/peer/aspire",
                Version = "13.7.0-pr.17999.gabc12345",
                Channel = "pr-17999",
                Source = "pr",
                PathStatus = InstallationPathStatus.Active,
                Status = InstallationInfoStatus.Ok,
            })));
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("--info --self --format json");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(CliExitCodes.Success, exitCode);

        var stdout = string.Join(Environment.NewLine, outputWriter.Logs);
        using var doc = System.Text.Json.JsonDocument.Parse(stdout);
        Assert.Equal(System.Text.Json.JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Equal(1, doc.RootElement.GetArrayLength());

        // Feed through the production peer-probe parser. Any drift in
        // property names, casing, or shape will fail here.
        var parsed = InstallationInfoParser.Parse(doc.RootElement[0]);
        Assert.Equal("/peer/aspire", parsed.Path);
        Assert.Equal("/peer/aspire", parsed.CanonicalPath);
        Assert.Equal("13.7.0-pr.17999.gabc12345", parsed.Version);
        Assert.Equal("pr-17999", parsed.Channel);
        Assert.Equal("pr", parsed.Source);
        Assert.Equal(InstallationPathStatus.Active, parsed.PathStatus);
        Assert.Equal(InstallationInfoStatus.Ok, parsed.Status);
    }

    private sealed class ConstantChannelReader(string channel) : IIdentityChannelReader
    {
        public string ReadChannel() => channel;
    }

    private sealed class ThrowingChannelReader : IIdentityChannelReader
    {
        public string ReadChannel() => throw new InvalidOperationException("AspireCliChannel assembly metadata is missing.");
    }
}
