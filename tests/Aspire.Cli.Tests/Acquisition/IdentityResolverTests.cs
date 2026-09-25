// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json;
using Aspire.Cli.Acquisition;
using Aspire.Cli.Packaging;
using Aspire.Cli.Tests.Utils;

namespace Aspire.Cli.Tests.Acquisition;

/// <summary>
/// Behavior tests for <see cref="IdentityResolver"/>. The resolver composes
/// three layers per field — environment variable, sidecar field, and the
/// assembly-baked fallback (or <see langword="null"/> for the NuGet override).
/// These tests pin the per-layer truth table so a refactor that quietly
/// swaps the precedence is caught immediately. See
/// <c>docs/specs/cli-identity-sidecar.md</c>.
/// </summary>
public class IdentityResolverTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void ResolveChannel_EnvWins_OverSidecarAndAssembly()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        WriteSidecar(workspace.WorkspaceRoot.FullName, """{"source":"script","channel":"staging"}""");

        var resolver = CreateResolver(workspace,
            channel: "stable",
            environmentVariables: new Dictionary<string, string?> { [IdentityResolver.ChannelEnvVar] = "pr-12345" });

        var resolved = resolver.ResolveChannel();
        Assert.Equal("pr-12345", resolved.Value);
        Assert.Equal(IdentitySource.Environment, resolved.Source);
    }

    [Fact]
    public void ResolveChannel_SidecarWins_OverAssembly_WhenEnvAbsent()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        WriteSidecar(workspace.WorkspaceRoot.FullName, """{"source":"script","channel":"staging"}""");

        var resolver = CreateResolver(workspace, channel: "stable");

        var resolved = resolver.ResolveChannel();
        Assert.Equal("staging", resolved.Value);
        Assert.Equal(IdentitySource.Sidecar, resolved.Source);
    }

    [Fact]
    public void ResolveChannel_AssemblyFallback_WhenSidecarAndEnvAbsent()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        // No sidecar file written — resolver should skip the sidecar layer.

        var resolver = CreateResolver(workspace, channel: "daily");

        var resolved = resolver.ResolveChannel();
        Assert.Equal("daily", resolved.Value);
        Assert.Equal(IdentitySource.AssemblyFallback, resolved.Source);
    }

    [Fact]
    public void ResolveChannel_TerminalDefault_WhenAllLayersEmpty()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        // Assembly without channel metadata throws inside IdentityChannelReader;
        // the resolver swallows that and falls through to the terminal default.
        var resolver = CreateResolver(workspace, channel: null);

        var resolved = resolver.ResolveChannel();
        Assert.Equal(PackageChannelNames.Local, resolved.Value);
        Assert.Equal(IdentitySource.TerminalDefault, resolved.Source);
    }

    [Fact]
    public void ResolveChannel_EmptyEnvIsTreatedAsAbsent()
    {
        // An empty string env var value must not shadow a real sidecar/assembly
        // value — otherwise a user un-setting via `set ASPIRE_CLI_CHANNEL=` on
        // Windows would silently force `local`.
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        WriteSidecar(workspace.WorkspaceRoot.FullName, """{"source":"script","channel":"staging"}""");

        var resolver = CreateResolver(workspace,
            channel: "stable",
            environmentVariables: new Dictionary<string, string?> { [IdentityResolver.ChannelEnvVar] = string.Empty });

        var resolved = resolver.ResolveChannel();
        Assert.Equal("staging", resolved.Value);
        Assert.Equal(IdentitySource.Sidecar, resolved.Source);
    }

    [Fact]
    public void ResolveVersion_SplitsInformationalVersionAtPlus()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var resolver = CreateResolver(workspace, informationalVersion: "13.4.0-preview.1.25366.3+abcdef0");

        var version = resolver.ResolveVersion();
        var commit = resolver.ResolveCommit();
        Assert.Equal("13.4.0-preview.1.25366.3", version.Value);
        Assert.Equal(IdentitySource.AssemblyFallback, version.Source);
        Assert.Equal("abcdef0", commit.Value);
        Assert.Equal(IdentitySource.AssemblyFallback, commit.Source);
    }

    [Fact]
    public void ResolveCommit_EmptyWhenInformationalVersionHasNoPlus()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var resolver = CreateResolver(workspace, informationalVersion: "13.4.0");

        Assert.Equal(string.Empty, resolver.ResolveCommit().Value);
        Assert.Equal("13.4.0", resolver.ResolveVersion().Value);
    }

    [Fact]
    public void ResolveVersion_EnvOverridesAssembly()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var resolver = CreateResolver(workspace,
            environmentVariables: new Dictionary<string, string?> { [IdentityResolver.VersionEnvVar] = "99.0.0-test" });

        var resolved = resolver.ResolveVersion();
        Assert.Equal("99.0.0-test", resolved.Value);
        Assert.Equal(IdentitySource.Environment, resolved.Source);
    }

    [Theory]
    [InlineData("13.4.3")]
    [InlineData("13.5.0-preview.1.26311.9")]
    [InlineData("13.4.0+abcdef0")]
    public void ResolveVersion_AcceptsValidSemVer(string version)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var resolver = CreateResolver(workspace,
            environmentVariables: new Dictionary<string, string?> { [IdentityResolver.VersionEnvVar] = version });

        var resolved = resolver.ResolveVersion();
        Assert.Equal(version, resolved.Value);
        Assert.Equal(IdentitySource.Environment, resolved.Source);
    }

    [Theory]
    [InlineData("not-a-version")]
    [InlineData("13.4")]
    [InlineData("13.4.0.0")]
    [InlineData("v13.4.0")]
    public void ResolveVersion_FromEnv_FailsFast_WhenNotAVersion(string version)
    {
        // The whole point of this PR's hardening: a bad ASPIRE_CLI_VERSION must surface
        // immediately with a message naming the env var, not silently corrupt downstream
        // version-keyed decisions.
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var resolver = CreateResolver(workspace,
            environmentVariables: new Dictionary<string, string?> { [IdentityResolver.VersionEnvVar] = version });

        var ex = Assert.Throws<InvalidOperationException>(() => resolver.ResolveVersion());
        Assert.Contains(IdentityResolver.VersionEnvVar, ex.Message);
        Assert.Contains(version, ex.Message);
    }

    [Fact]
    public void ResolveVersion_FromSidecar_FailsFast_WhenNotAVersion()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        WriteSidecar(workspace.WorkspaceRoot.FullName, """{"source":"script","version":"garbage"}""");
        var resolver = CreateResolver(workspace);

        var ex = Assert.Throws<InvalidOperationException>(() => resolver.ResolveVersion());
        Assert.Contains(InstallSidecarReader.SidecarFileName, ex.Message);
        Assert.Contains("version", ex.Message);
    }

    [Theory]
    [InlineData("abcdef01")]
    [InlineData("ABCDEF0123456789")]
    [InlineData("0123456789012345678901234567890123456789")]
    public void ResolveCommit_AcceptsHexSha(string commit)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var resolver = CreateResolver(workspace,
            environmentVariables: new Dictionary<string, string?> { [IdentityResolver.CommitEnvVar] = commit });

        var resolved = resolver.ResolveCommit();
        Assert.Equal(commit, resolved.Value);
        Assert.Equal(IdentitySource.Environment, resolved.Source);
    }

    [Theory]
    [InlineData("xyz1234")]        // non-hex characters
    [InlineData("abc")]            // too short (< 8)
    [InlineData("abcdef0")]        // 7 hex chars: one short of the 8-char darc feed contract
    [InlineData("abcdef 0")]       // embedded space
    public void ResolveCommit_FromEnv_FailsFast_WhenNotHex(string commit)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var resolver = CreateResolver(workspace,
            environmentVariables: new Dictionary<string, string?> { [IdentityResolver.CommitEnvVar] = commit });

        var ex = Assert.Throws<InvalidOperationException>(() => resolver.ResolveCommit());
        Assert.Contains(IdentityResolver.CommitEnvVar, ex.Message);
    }

    [Theory]
    [InlineData("http://127.0.0.1:5400/v3/index.json")]
    [InlineData("https://api.nuget.org/v3/index.json")]
    public void ResolveNuGetServiceIndexOverride_AcceptsAbsoluteHttpUrl(string url)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var resolver = CreateResolver(workspace,
            environmentVariables: new Dictionary<string, string?> { [IdentityResolver.NuGetServiceIndexEnvVar] = url });

        var resolved = resolver.ResolveNuGetServiceIndexOverride();
        Assert.Equal(url, resolved.Value);
        Assert.Equal(IdentitySource.Environment, resolved.Source);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("ftp://host/v3/index.json")]    // wrong scheme
    [InlineData("/relative/v3/index.json")]     // not absolute
    public void ResolveNuGetServiceIndexOverride_FromEnv_FailsFast_WhenNotHttpUrl(string url)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var resolver = CreateResolver(workspace,
            environmentVariables: new Dictionary<string, string?> { [IdentityResolver.NuGetServiceIndexEnvVar] = url });

        var ex = Assert.Throws<InvalidOperationException>(() => resolver.ResolveNuGetServiceIndexOverride());
        Assert.Contains(IdentityResolver.NuGetServiceIndexEnvVar, ex.Message);
    }

    [Fact]
    public void ResolveChannel_FromEnv_AcceptsBespokeLabel_WithoutValidation()
    {
        // Channel is intentionally NOT shape-validated from env/sidecar: bespoke labels like
        // "pr-17580" are legitimate overrides. This pins that decision so a future "tighten
        // validation" change can't silently break the override's primary use case.
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var resolver = CreateResolver(workspace,
            environmentVariables: new Dictionary<string, string?> { [IdentityResolver.ChannelEnvVar] = "totally-made-up" },
            channel: "stable",
            assemblyName: "ChannelBespoke");

        var resolved = resolver.ResolveChannel();
        Assert.Equal("totally-made-up", resolved.Value);
        Assert.Equal(IdentitySource.Environment, resolved.Source);
    }

    [Fact]
    public void ResolveNuGetServiceIndexOverride_NullByDefault()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        // No sidecar field, no env var — the override must remain null so
        // callers fall back to PackageSources.NuGetOrg via the `?? canonical` pattern.
        var resolver = CreateResolver(workspace, assemblyName: "OverrideNull");

        var resolved = resolver.ResolveNuGetServiceIndexOverride();
        Assert.Null(resolved.Value);
        Assert.Equal(IdentitySource.TerminalDefault, resolved.Source);
    }

    [Fact]
    public void ResolveNuGetServiceIndexOverride_EnvWins()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        WriteSidecar(workspace.WorkspaceRoot.FullName, """{"source":"script","nugetServiceIndexOverride":"http://sidecar/v3/index.json"}""");

        var resolver = CreateResolver(workspace,
            environmentVariables: new Dictionary<string, string?> { [IdentityResolver.NuGetServiceIndexEnvVar] = "http://env/v3/index.json" },
            assemblyName: "OverrideEnv");

        var resolved = resolver.ResolveNuGetServiceIndexOverride();
        Assert.Equal("http://env/v3/index.json", resolved.Value);
        Assert.Equal(IdentitySource.Environment, resolved.Source);
    }

    [Fact]
    public void ResolveNuGetServiceIndexOverride_SidecarUsedWhenEnvAbsent()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        WriteSidecar(workspace.WorkspaceRoot.FullName, """{"source":"script","nugetServiceIndexOverride":"http://proxy.local/v3/index.json"}""");

        var resolver = CreateResolver(workspace, assemblyName: "OverrideSc");

        var resolved = resolver.ResolveNuGetServiceIndexOverride();
        Assert.Equal("http://proxy.local/v3/index.json", resolved.Value);
        Assert.Equal(IdentitySource.Sidecar, resolved.Source);
    }

    [Fact]
    public void ResolvePackagesDirectory_NullByDefault()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        // No sidecar field, no env var — the override must remain null so the
        // packaging service does not synthesize an override channel.
        var resolver = CreateResolver(workspace, assemblyName: "PackagesNull");

        var resolved = resolver.ResolvePackagesDirectory();
        Assert.Null(resolved.Value);
        Assert.Equal(IdentitySource.TerminalDefault, resolved.Source);
    }

    [Fact]
    public void ResolvePackagesDirectory_EnvWins()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        WriteSidecar(workspace.WorkspaceRoot.FullName, """{"source":"script","packages":"/sidecar/packages"}""");

        var resolver = CreateResolver(workspace,
            environmentVariables: new Dictionary<string, string?> { [IdentityResolver.PackagesEnvVar] = "/env/packages" },
            assemblyName: "PackagesEnv");

        var resolved = resolver.ResolvePackagesDirectory();
        Assert.Equal("/env/packages", resolved.Value);
        Assert.Equal(IdentitySource.Environment, resolved.Source);
    }

    [Fact]
    public void ResolvePackagesDirectory_SidecarUsedWhenEnvAbsent()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        WriteSidecar(workspace.WorkspaceRoot.FullName, """{"source":"script","packages":"/sidecar/packages"}""");

        var resolver = CreateResolver(workspace, assemblyName: "PackagesSc");

        var resolved = resolver.ResolvePackagesDirectory();
        Assert.Equal("/sidecar/packages", resolved.Value);
        Assert.Equal(IdentitySource.Sidecar, resolved.Source);
    }

    /// <summary>
    /// Pins the warning policy independently for every source and field. Sidecar
    /// channel/version/commit values are canonical installed identity, while every
    /// environment value and both package-redirection fields are diagnostic overrides.
    /// Keeping the rows explicit makes adding a future identity field require a conscious
    /// warning-policy decision instead of silently inheriting whichever boolean is convenient.
    /// </summary>
    [Theory]
    // Installers and self-update author these fields. Any one of them is authoritative
    // installed identity and must not make a normal staging installation look emulated.
    // The sidecar has no authorship marker, so a manual edit follows the same policy.
    [InlineData(IdentityInputSource.Sidecar, IdentityField.Channel, false)]
    [InlineData(IdentityInputSource.Sidecar, IdentityField.Version, false)]
    [InlineData(IdentityInputSource.Sidecar, IdentityField.Commit, false)]
    // These sidecar fields redirect package behavior and are developer test affordances,
    // so their persistent nature does not make them part of a standard installation.
    [InlineData(IdentityInputSource.Sidecar, IdentityField.NuGetServiceIndexOverride, true)]
    [InlineData(IdentityInputSource.Sidecar, IdentityField.Packages, true)]
    // Environment variables always describe process-local emulation, even when their value
    // happens to match the installed identity, because ambient shell state can be accidental.
    [InlineData(IdentityInputSource.Environment, IdentityField.Channel, true)]
    [InlineData(IdentityInputSource.Environment, IdentityField.Version, true)]
    [InlineData(IdentityInputSource.Environment, IdentityField.Commit, true)]
    [InlineData(IdentityInputSource.Environment, IdentityField.NuGetServiceIndexOverride, true)]
    [InlineData(IdentityInputSource.Environment, IdentityField.Packages, true)]
    public void BuildCliExecutionContext_IdentityOverrideNoticePolicy_MatchesSourceAndFieldSemantics(
        IdentityInputSource inputSource,
        IdentityField field,
        bool expectedNoticeRequired)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var value = GetIdentityValue(workspace, field);
        IReadOnlyDictionary<string, string?>? environmentVariables = null;

        if (inputSource is IdentityInputSource.Sidecar)
        {
            var sidecar = new Dictionary<string, string>
            {
                ["source"] = "script",
                [GetSidecarFieldName(field)] = value,
            };
            WriteSidecar(workspace.WorkspaceRoot.FullName, JsonSerializer.Serialize(sidecar));
        }
        else
        {
            environmentVariables = new Dictionary<string, string?>
            {
                [GetEnvironmentVariableName(field)] = value,
            };
        }

        var resolver = CreateResolver(
            workspace,
            environmentVariables: environmentVariables,
            informationalVersion: "13.5.0-dev+abcdef01",
            assemblyName: $"NoticePolicy{inputSource}{field}");

        var context = BuildContextFromResolver(workspace, resolver);

        Assert.Equal(
            inputSource is IdentityInputSource.Sidecar ? IdentitySource.Sidecar : IdentitySource.Environment,
            GetResolvedSource(resolver, field));
        Assert.True(context.IdentityOverridden);
        Assert.Equal(expectedNoticeRequired, context.IdentityOverrideNoticeRequired);
    }

    [Fact]
    public void BuildCliExecutionContext_AllInstalledSidecarIdentityFields_DoNotRequireOverrideNotice()
    {
        // Installers may eventually author all three fields together. The policy applies to
        // any combination, not just the channel-only shape written by today's staging flow.
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        WriteSidecar(
            workspace.WorkspaceRoot.FullName,
            """{"source":"script","channel":"staging","version":"13.5.0","commit":"abcdef01"}""");
        var resolver = CreateResolver(
            workspace,
            channel: "stable",
            informationalVersion: "13.4.6+12345678",
            assemblyName: "AllInstalledSidecarIdentity");

        var context = BuildContextFromResolver(workspace, resolver);

        Assert.True(context.IdentityOverridden);
        Assert.False(context.IdentityOverrideNoticeRequired);
        Assert.Equal("staging", context.IdentityChannel);
        Assert.Equal("13.5.0", context.IdentityVersion);
        Assert.Equal("abcdef01", context.IdentityCommit);
    }

    [Fact]
    public void BuildCliExecutionContext_LegacySourceOnlySidecar_DoesNotRequireOverrideNotice()
    {
        // A legacy route sidecar contributes no identity. Falling back to assembly metadata
        // may warrant a doctor diagnostic, but it is not evidence of active emulation.
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        WriteSidecar(workspace.WorkspaceRoot.FullName, """{"source":"script"}""");
        var resolver = CreateResolver(
            workspace,
            channel: "stable",
            informationalVersion: "13.4.6+12345678",
            assemblyName: "LegacySourceOnlySidecar");

        var context = BuildContextFromResolver(workspace, resolver);

        Assert.False(context.IdentityOverridden);
        Assert.False(context.IdentityOverrideNoticeRequired);
    }

    [Fact]
    public void BuildCliExecutionContext_InstalledIdentityWithDiagnosticOverride_RequiresOverrideNotice()
    {
        // One diagnostic input is enough to warn, even when the base channel/version/commit
        // correctly describe a standard staging installation.
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        WriteSidecar(
            workspace.WorkspaceRoot.FullName,
            """{"source":"script","channel":"staging","version":"13.5.0","commit":"abcdef01"}""");
        var packagesDirectory = workspace.CreateDirectory("identity-packages");
        var resolver = CreateResolver(
            workspace,
            environmentVariables: new Dictionary<string, string?>
            {
                [IdentityResolver.PackagesEnvVar] = packagesDirectory.FullName,
            },
            assemblyName: "InstalledIdentityWithDiagnosticOverride");

        var context = BuildContextFromResolver(workspace, resolver);

        Assert.True(context.IdentityOverridden);
        Assert.True(context.IdentityOverrideNoticeRequired);
        Assert.Equal("staging", context.IdentityChannel);
        Assert.Equal(packagesDirectory.FullName, context.IdentityPackagesDirectory!.FullName);
    }

    [Fact]
    public void BuildCliExecutionContext_DoesNotFlagIdentityOverridden_WhenAssemblyOnly()
    {
        // No env vars and no sidecar — a real install reads its own assembly stamp, so the
        // notice must stay silent.
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var resolver = CreateResolver(workspace,
            channel: "daily",
            informationalVersion: "13.5.0-preview.1.25366.3+abcdef0",
            assemblyName: "AssemblyOnly");

        var context = BuildContextFromResolver(workspace, resolver);

        Assert.False(context.IdentityOverridden);
        Assert.False(context.IdentityOverrideNoticeRequired);
    }

    [Fact]
    public void IdentityEnvVarNames_ContainsAllFiveOverrides()
    {
        // The strip-list used by PeerInstallProbe / ProcessExecutionFactory must
        // cover every override the resolver reads — otherwise a leaked env var
        // would still corrupt child processes. Pinning the set guards against
        // an unbalanced add (new constant above, missed below) which would
        // pass build but quietly defeat the leak guarantee.
        Assert.Equal(
            new[]
            {
                IdentityResolver.ChannelEnvVar,
                IdentityResolver.VersionEnvVar,
                IdentityResolver.CommitEnvVar,
                IdentityResolver.NuGetServiceIndexEnvVar,
                IdentityResolver.PackagesEnvVar,
            },
            IdentityResolver.IdentityEnvVarNames);
    }

    private static void WriteSidecar(string directory, string json)
        => File.WriteAllText(Path.Combine(directory, InstallSidecarReader.SidecarFileName), json);

    private static string GetIdentityValue(TemporaryWorkspace workspace, IdentityField field)
        => field switch
        {
            IdentityField.Channel => "staging",
            IdentityField.Version => "13.5.0",
            IdentityField.Commit => "abcdef01",
            IdentityField.NuGetServiceIndexOverride => "https://example.test/v3/index.json",
            IdentityField.Packages => workspace.CreateDirectory("identity-packages").FullName,
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };

    private static string GetSidecarFieldName(IdentityField field)
        => field switch
        {
            IdentityField.Channel => "channel",
            IdentityField.Version => "version",
            IdentityField.Commit => "commit",
            IdentityField.NuGetServiceIndexOverride => "nugetServiceIndexOverride",
            IdentityField.Packages => "packages",
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };

    private static string GetEnvironmentVariableName(IdentityField field)
        => field switch
        {
            IdentityField.Channel => IdentityResolver.ChannelEnvVar,
            IdentityField.Version => IdentityResolver.VersionEnvVar,
            IdentityField.Commit => IdentityResolver.CommitEnvVar,
            IdentityField.NuGetServiceIndexOverride => IdentityResolver.NuGetServiceIndexEnvVar,
            IdentityField.Packages => IdentityResolver.PackagesEnvVar,
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };

    private static IdentitySource GetResolvedSource(IIdentityResolver resolver, IdentityField field)
        => field switch
        {
            IdentityField.Channel => resolver.ResolveChannel().Source,
            IdentityField.Version => resolver.ResolveVersion().Source,
            IdentityField.Commit => resolver.ResolveCommit().Source,
            IdentityField.NuGetServiceIndexOverride => resolver.ResolveNuGetServiceIndexOverride().Source,
            IdentityField.Packages => resolver.ResolvePackagesDirectory().Source,
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };

    private IdentityResolver CreateResolver(
        TemporaryWorkspace workspace,
        IReadOnlyDictionary<string, string?>? environmentVariables = null,
        string? channel = "local",
        string informationalVersion = "13.4.0+abc",
        string assemblyName = "Test")
    {
        var environment = new TestEnvironment(environmentVariables);

        return new IdentityResolver(
            CliTestHelper.CreateSidecarReader(outputHelper),
            BuildAssembly(assemblyName, channel, informationalVersion),
            workspace.WorkspaceRoot.FullName,
            environment);
    }

    /// <summary>
    /// Exercises the real <c>Program.BuildCliExecutionContext(resolver)</c> production path
    /// so that the identity-override OR-computation and directory derivation are tested
    /// against the actual implementation rather than a local copy.
    /// </summary>
    private static CliExecutionContext BuildContextFromResolver(TemporaryWorkspace workspace, IIdentityResolver resolver)
    {
        return Program.BuildCliExecutionContext(
            debugMode: false,
            consoleLogLevel: null,
            logsDirectory: Path.Combine(workspace.WorkspaceRoot.FullName, "logs"),
            logFilePath: Path.Combine(workspace.WorkspaceRoot.FullName, "logs", "test.log"),
            identityResolver: resolver);
    }

    private static Assembly BuildAssembly(string assemblyName, string? channel, string informationalVersion)
    {
        var name = new AssemblyName(assemblyName);
        var builder = AssemblyBuilder.DefineDynamicAssembly(name, AssemblyBuilderAccess.Run);

        var metaCtor = typeof(AssemblyMetadataAttribute).GetConstructor([typeof(string), typeof(string)])!;
        if (channel is not null)
        {
            builder.SetCustomAttribute(new CustomAttributeBuilder(metaCtor, ["AspireCliChannel", channel]));
        }

        var infoCtor = typeof(AssemblyInformationalVersionAttribute).GetConstructor([typeof(string)])!;
        builder.SetCustomAttribute(new CustomAttributeBuilder(infoCtor, [informationalVersion]));

        return builder;
    }

    public enum IdentityInputSource
    {
        Sidecar,
        Environment,
    }

    public enum IdentityField
    {
        Channel,
        Version,
        Commit,
        NuGetServiceIndexOverride,
        Packages,
    }
}
