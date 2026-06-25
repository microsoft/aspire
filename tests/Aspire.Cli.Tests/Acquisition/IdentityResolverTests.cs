// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using System.Reflection.Emit;
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
        using var workspace = TemporaryWorkspace.Create(outputHelper);
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
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        WriteSidecar(workspace.WorkspaceRoot.FullName, """{"source":"script","channel":"staging"}""");

        var resolver = CreateResolver(workspace, channel: "stable");

        var resolved = resolver.ResolveChannel();
        Assert.Equal("staging", resolved.Value);
        Assert.Equal(IdentitySource.Sidecar, resolved.Source);
    }

    [Fact]
    public void ResolveChannel_AssemblyFallback_WhenSidecarAndEnvAbsent()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        // No sidecar file written — resolver should skip the sidecar layer.

        var resolver = CreateResolver(workspace, channel: "daily");

        var resolved = resolver.ResolveChannel();
        Assert.Equal("daily", resolved.Value);
        Assert.Equal(IdentitySource.AssemblyFallback, resolved.Source);
    }

    [Fact]
    public void ResolveChannel_TerminalDefault_WhenAllLayersEmpty()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

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
        using var workspace = TemporaryWorkspace.Create(outputHelper);
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
        using var workspace = TemporaryWorkspace.Create(outputHelper);
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
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var resolver = CreateResolver(workspace, informationalVersion: "13.4.0");

        Assert.Equal(string.Empty, resolver.ResolveCommit().Value);
        Assert.Equal("13.4.0", resolver.ResolveVersion().Value);
    }

    [Fact]
    public void ResolveVersion_EnvOverridesAssembly()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
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
        using var workspace = TemporaryWorkspace.Create(outputHelper);
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
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var resolver = CreateResolver(workspace,
            environmentVariables: new Dictionary<string, string?> { [IdentityResolver.VersionEnvVar] = version });

        var ex = Assert.Throws<InvalidOperationException>(() => resolver.ResolveVersion());
        Assert.Contains(IdentityResolver.VersionEnvVar, ex.Message);
        Assert.Contains(version, ex.Message);
    }

    [Fact]
    public void ResolveVersion_FromSidecar_FailsFast_WhenNotAVersion()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
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
        using var workspace = TemporaryWorkspace.Create(outputHelper);
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
        using var workspace = TemporaryWorkspace.Create(outputHelper);
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
        using var workspace = TemporaryWorkspace.Create(outputHelper);
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
        using var workspace = TemporaryWorkspace.Create(outputHelper);
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
        using var workspace = TemporaryWorkspace.Create(outputHelper);
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
        using var workspace = TemporaryWorkspace.Create(outputHelper);
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
        using var workspace = TemporaryWorkspace.Create(outputHelper);
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
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        WriteSidecar(workspace.WorkspaceRoot.FullName, """{"source":"script","nugetServiceIndexOverride":"http://proxy.local/v3/index.json"}""");

        var resolver = CreateResolver(workspace, assemblyName: "OverrideSc");

        var resolved = resolver.ResolveNuGetServiceIndexOverride();
        Assert.Equal("http://proxy.local/v3/index.json", resolved.Value);
        Assert.Equal(IdentitySource.Sidecar, resolved.Source);
    }

    [Fact]
    public void ResolvePackagesDirectory_NullByDefault()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
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
        using var workspace = TemporaryWorkspace.Create(outputHelper);
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
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        WriteSidecar(workspace.WorkspaceRoot.FullName, """{"source":"script","packages":"/sidecar/packages"}""");

        var resolver = CreateResolver(workspace, assemblyName: "PackagesSc");

        var resolved = resolver.ResolvePackagesDirectory();
        Assert.Equal("/sidecar/packages", resolved.Value);
        Assert.Equal(IdentitySource.Sidecar, resolved.Source);
    }

    [Fact]
    public void BuildCliExecutionContext_FlagsIdentityOverridden_WhenEnvVersionSupplied()
    {
        // ASPIRE_CLI_VERSION emulation must light up the override notice and feed IdentityVersion.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var envVars = new Dictionary<string, string?> { [IdentityResolver.VersionEnvVar] = "13.4.2" };
        var resolver = CreateResolver(workspace,
            environmentVariables: envVars,
            informationalVersion: "13.5.0-dev+local",
            assemblyName: "EnvVersionOverride");

        var context = BuildContextFromResolver(workspace, resolver);

        Assert.True(context.IdentityOverridden);
        Assert.Equal("13.4.2", context.IdentityVersion);
    }

    [Fact]
    public void BuildCliExecutionContext_FlagsIdentityOverridden_WhenSidecarChannelSupplied()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        WriteSidecar(workspace.WorkspaceRoot.FullName, """{"source":"script","channel":"staging"}""");
        var resolver = CreateResolver(workspace,
            informationalVersion: "13.5.0-dev+local",
            assemblyName: "SidecarChannelOverride");

        var context = BuildContextFromResolver(workspace, resolver);

        Assert.True(context.IdentityOverridden);
        Assert.Equal("staging", context.IdentityChannel);
    }

    [Fact]
    public void BuildCliExecutionContext_DoesNotFlagIdentityOverridden_WhenAssemblyOnly()
    {
        // No env vars and no sidecar — a real install reads its own assembly stamp, so the
        // notice must stay silent.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var resolver = CreateResolver(workspace,
            channel: "daily",
            informationalVersion: "13.5.0-preview.1.25366.3+abcdef0",
            assemblyName: "AssemblyOnly");

        var context = BuildContextFromResolver(workspace, resolver);

        Assert.False(context.IdentityOverridden);
    }

    [Fact]
    public void BuildCliExecutionContext_FlagsIdentityOverridden_AndSetsPackagesDirectory_WhenPackagesOverrideSupplied()
    {
        // ASPIRE_CLI_PACKAGES emulation must light up the override notice and surface the directory
        // so PackagingService can synthesize an override channel from it.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var packagesDir = Path.Combine(workspace.WorkspaceRoot.FullName, "shipping");
        var envVars = new Dictionary<string, string?> { [IdentityResolver.PackagesEnvVar] = packagesDir };
        var resolver = CreateResolver(workspace,
            environmentVariables: envVars,
            informationalVersion: "13.5.0-dev+local",
            assemblyName: "EnvPackagesOverride");

        var context = BuildContextFromResolver(workspace, resolver);

        Assert.True(context.IdentityOverridden);
        Assert.NotNull(context.IdentityPackagesDirectory);
        Assert.Equal(packagesDir, context.IdentityPackagesDirectory!.FullName);
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
}
