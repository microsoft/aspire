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

        var resolver = new IdentityResolver(
            new InstallSidecarReader(),
            BuildAssembly("EnvWins", channel: "stable", informationalVersion: "13.4.0+abc"),
            workspace.WorkspaceRoot.FullName,
            envReader: name => name == IdentityResolver.ChannelEnvVar ? "pr-12345" : null);

        var resolved = resolver.ResolveChannel();
        Assert.Equal("pr-12345", resolved.Value);
        Assert.Equal(IdentitySource.Environment, resolved.Source);
    }

    [Fact]
    public void ResolveChannel_SidecarWins_OverAssembly_WhenEnvAbsent()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        WriteSidecar(workspace.WorkspaceRoot.FullName, """{"source":"script","channel":"staging"}""");

        var resolver = new IdentityResolver(
            new InstallSidecarReader(),
            BuildAssembly("SidecarWins", channel: "stable", informationalVersion: "13.4.0+abc"),
            workspace.WorkspaceRoot.FullName,
            envReader: _ => null);

        var resolved = resolver.ResolveChannel();
        Assert.Equal("staging", resolved.Value);
        Assert.Equal(IdentitySource.Sidecar, resolved.Source);
    }

    [Fact]
    public void ResolveChannel_AssemblyFallback_WhenSidecarAndEnvAbsent()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        // No sidecar file written — resolver should skip the sidecar layer.

        var resolver = new IdentityResolver(
            new InstallSidecarReader(),
            BuildAssembly("AsmFallback", channel: "daily", informationalVersion: "13.4.0+abc"),
            workspace.WorkspaceRoot.FullName,
            envReader: _ => null);

        var resolved = resolver.ResolveChannel();
        Assert.Equal("daily", resolved.Value);
        Assert.Equal(IdentitySource.AssemblyFallback, resolved.Source);
    }

    [Fact]
    public void ResolveChannel_TerminalDefault_WhenAllLayersEmpty()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var resolver = new IdentityResolver(
            new InstallSidecarReader(),
            // Assembly without channel metadata throws inside IdentityChannelReader;
            // the resolver swallows that and falls through to the terminal default.
            BuildAssembly("NoChannel", channel: null, informationalVersion: "13.4.0+abc"),
            workspace.WorkspaceRoot.FullName,
            envReader: _ => null);

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

        var resolver = new IdentityResolver(
            new InstallSidecarReader(),
            BuildAssembly("EmptyEnv", channel: "stable", informationalVersion: "13.4.0+abc"),
            workspace.WorkspaceRoot.FullName,
            envReader: name => name == IdentityResolver.ChannelEnvVar ? string.Empty : null);

        var resolved = resolver.ResolveChannel();
        Assert.Equal("staging", resolved.Value);
        Assert.Equal(IdentitySource.Sidecar, resolved.Source);
    }

    [Fact]
    public void ResolveVersion_SplitsInformationalVersionAtPlus()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var resolver = new IdentityResolver(
            new InstallSidecarReader(),
            BuildAssembly("VersionSplit", channel: "local", informationalVersion: "13.4.0-preview.1.25366.3+abcdef0"),
            workspace.WorkspaceRoot.FullName,
            envReader: _ => null);

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
        var resolver = new IdentityResolver(
            new InstallSidecarReader(),
            BuildAssembly("NoCommit", channel: "local", informationalVersion: "13.4.0"),
            workspace.WorkspaceRoot.FullName,
            envReader: _ => null);

        Assert.Equal(string.Empty, resolver.ResolveCommit().Value);
        Assert.Equal("13.4.0", resolver.ResolveVersion().Value);
    }

    [Fact]
    public void ResolveVersion_EnvOverridesAssembly()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var resolver = new IdentityResolver(
            new InstallSidecarReader(),
            BuildAssembly("VerEnv", channel: "local", informationalVersion: "13.4.0+abc"),
            workspace.WorkspaceRoot.FullName,
            envReader: name => name == IdentityResolver.VersionEnvVar ? "99.0.0-test" : null);

        var resolved = resolver.ResolveVersion();
        Assert.Equal("99.0.0-test", resolved.Value);
        Assert.Equal(IdentitySource.Environment, resolved.Source);
    }

    [Fact]
    public void ResolveNuGetServiceIndexOverride_NullByDefault()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        // No sidecar field, no env var — the override must remain null so
        // callers fall back to PackageSources.NuGetOrg via the `?? canonical` pattern.
        var resolver = new IdentityResolver(
            new InstallSidecarReader(),
            BuildAssembly("OverrideNull", channel: "local", informationalVersion: "13.4.0+abc"),
            workspace.WorkspaceRoot.FullName,
            envReader: _ => null);

        var resolved = resolver.ResolveNuGetServiceIndexOverride();
        Assert.Null(resolved.Value);
        Assert.Equal(IdentitySource.TerminalDefault, resolved.Source);
    }

    [Fact]
    public void ResolveNuGetServiceIndexOverride_EnvWins()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        WriteSidecar(workspace.WorkspaceRoot.FullName, """{"source":"script","nugetServiceIndexOverride":"http://sidecar/v3/index.json"}""");

        var resolver = new IdentityResolver(
            new InstallSidecarReader(),
            BuildAssembly("OverrideEnv", channel: "local", informationalVersion: "13.4.0+abc"),
            workspace.WorkspaceRoot.FullName,
            envReader: name => name == IdentityResolver.NuGetServiceIndexEnvVar
                ? "http://env/v3/index.json"
                : null);

        var resolved = resolver.ResolveNuGetServiceIndexOverride();
        Assert.Equal("http://env/v3/index.json", resolved.Value);
        Assert.Equal(IdentitySource.Environment, resolved.Source);
    }

    [Fact]
    public void ResolveNuGetServiceIndexOverride_SidecarUsedWhenEnvAbsent()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        WriteSidecar(workspace.WorkspaceRoot.FullName, """{"source":"script","nugetServiceIndexOverride":"http://proxy.local/v3/index.json"}""");

        var resolver = new IdentityResolver(
            new InstallSidecarReader(),
            BuildAssembly("OverrideSc", channel: "local", informationalVersion: "13.4.0+abc"),
            workspace.WorkspaceRoot.FullName,
            envReader: _ => null);

        var resolved = resolver.ResolveNuGetServiceIndexOverride();
        Assert.Equal("http://proxy.local/v3/index.json", resolved.Value);
        Assert.Equal(IdentitySource.Sidecar, resolved.Source);
    }

    [Fact]
    public void IdentityEnvVarNames_ContainsAllFourOverrides()
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
            },
            IdentityResolver.IdentityEnvVarNames);
    }

    private static void WriteSidecar(string directory, string json)
        => File.WriteAllText(Path.Combine(directory, InstallSidecarReader.SidecarFileName), json);

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
