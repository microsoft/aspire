// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using Aspire.Cli.Packaging;
using Aspire.Shared;
using Semver;

namespace Aspire.Cli.Acquisition;

/// <summary>
/// Default <see cref="IIdentityResolver"/>. Reads in priority order:
/// environment variable → sidecar field → assembly-baked fallback (or
/// <see langword="null"/> for the NuGet service-index override).
/// </summary>
/// <remarks>
/// <para>
/// Each resolved field is cached for the lifetime of the resolver instance
/// via <see cref="Lazy{T}"/>. The resolver is a DI singleton so reads happen
/// at most once per CLI process; tests substitute a fake implementation.
/// </para>
/// <para>
/// The env-reader is injected as a delegate rather than read straight from
/// <see cref="Environment.GetEnvironmentVariable(string)"/> so tests can run
/// in parallel without racing against a shared process-environment.
/// Production wiring in <c>Program.cs</c> passes
/// <c>Environment.GetEnvironmentVariable</c> directly.
/// </para>
/// </remarks>
internal sealed class IdentityResolver : IIdentityResolver
{
    // Env var name constants live in the shared file so external tooling
    // (tools/Dogfooder) can author the same vars without taking a project
    // reference on the CLI. The aliases below preserve the resolver's
    // previous public surface so existing callers and tests compile unchanged.
    internal const string ChannelEnvVar = AspireCliIdentityEnvVars.Channel;
    internal const string VersionEnvVar = AspireCliIdentityEnvVars.Version;
    internal const string CommitEnvVar = AspireCliIdentityEnvVars.Commit;
    internal const string NuGetServiceIndexEnvVar = AspireCliIdentityEnvVars.NuGetServiceIndex;
    internal const string PackagesEnvVar = AspireCliIdentityEnvVars.Packages;

    /// <summary>
    /// The full set of <c>ASPIRE_CLI_*</c> identity-override environment
    /// variables that the CLI strips before spawning child Aspire processes
    /// (see <c>PeerInstallProbe</c>). Centralised so the strip-list stays in
    /// lockstep with the resolver's read-list — if you add a new override
    /// constant above, it shows up here automatically.
    /// </summary>
    internal static IReadOnlyList<string> IdentityEnvVarNames => AspireCliIdentityEnvVars.IdentityEnvVarNames;

    // The set of channel strings the assembly-baked fallback may legally
    // produce. We intentionally do NOT validate env / sidecar channel values
    // against this set: tests and developer overrides routinely use bespoke
    // channel labels (e.g. "pr-17580") and rejecting them here would defeat
    // the override's purpose. The assembly metadata reader (below) does
    // validate, because that is the one input we control end-to-end.
    private readonly IInstallSidecarReader _sidecarReader;
    private readonly Assembly _assembly;
    private readonly string? _binaryDir;
    private readonly Func<string, string?> _envReader;

    private readonly Lazy<InstallSidecarInfo?> _sidecar;
    private readonly Lazy<string> _assemblyChannel;
    private readonly Lazy<(string Version, string Commit)> _assemblyVersionAndCommit;

    private readonly Lazy<IdentityValue<string>> _channel;
    private readonly Lazy<IdentityValue<string>> _version;
    private readonly Lazy<IdentityValue<string>> _commit;
    private readonly Lazy<IdentityValue<string?>> _nugetServiceIndexOverride;
    private readonly Lazy<IdentityValue<string?>> _packagesDirectory;

    public IdentityResolver(
        IInstallSidecarReader sidecarReader,
        Assembly assembly,
        string? binaryDir,
        Func<string, string?>? envReader = null)
    {
        ArgumentNullException.ThrowIfNull(sidecarReader);
        ArgumentNullException.ThrowIfNull(assembly);

        _sidecarReader = sidecarReader;
        _assembly = assembly;
        _binaryDir = binaryDir;
        _envReader = envReader ?? Environment.GetEnvironmentVariable;

        _sidecar = new Lazy<InstallSidecarInfo?>(LoadSidecar, LazyThreadSafetyMode.ExecutionAndPublication);
        _assemblyChannel = new Lazy<string>(LoadAssemblyChannel, LazyThreadSafetyMode.ExecutionAndPublication);
        _assemblyVersionAndCommit = new Lazy<(string, string)>(LoadAssemblyVersionAndCommit, LazyThreadSafetyMode.ExecutionAndPublication);

        _channel = new Lazy<IdentityValue<string>>(ResolveChannelCore, LazyThreadSafetyMode.ExecutionAndPublication);
        _version = new Lazy<IdentityValue<string>>(ResolveVersionCore, LazyThreadSafetyMode.ExecutionAndPublication);
        _commit = new Lazy<IdentityValue<string>>(ResolveCommitCore, LazyThreadSafetyMode.ExecutionAndPublication);
        _nugetServiceIndexOverride = new Lazy<IdentityValue<string?>>(ResolveNuGetServiceIndexOverrideCore, LazyThreadSafetyMode.ExecutionAndPublication);
        _packagesDirectory = new Lazy<IdentityValue<string?>>(ResolvePackagesDirectoryCore, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <inheritdoc />
    public IdentityValue<string> ResolveChannel() => _channel.Value;

    /// <inheritdoc />
    public IdentityValue<string> ResolveVersion() => _version.Value;

    /// <inheritdoc />
    public IdentityValue<string> ResolveCommit() => _commit.Value;

    /// <inheritdoc />
    public IdentityValue<string?> ResolveNuGetServiceIndexOverride() => _nugetServiceIndexOverride.Value;

    /// <inheritdoc />
    public IdentityValue<string?> ResolvePackagesDirectory() => _packagesDirectory.Value;

    private IdentityValue<string> ResolveChannelCore()
    {
        if (TryGetEnv(ChannelEnvVar, out var env))
        {
            return new IdentityValue<string>(env, IdentitySource.Environment);
        }

        var sidecarValue = _sidecar.Value?.Channel;
        if (!string.IsNullOrEmpty(sidecarValue))
        {
            return new IdentityValue<string>(sidecarValue, IdentitySource.Sidecar);
        }

        var assemblyValue = _assemblyChannel.Value;
        if (!string.IsNullOrEmpty(assemblyValue))
        {
            // The assembly default for non-CI builds is "local", so this also
            // covers the dev-tree `dotnet run --project src/Aspire.Cli` case.
            return new IdentityValue<string>(assemblyValue, IdentitySource.AssemblyFallback);
        }

        return new IdentityValue<string>(PackageChannelNames.Local, IdentitySource.TerminalDefault);
    }

    private IdentityValue<string> ResolveVersionCore()
    {
        if (TryGetEnv(VersionEnvVar, out var env))
        {
            ValidateVersion(env, IdentitySource.Environment);
            return new IdentityValue<string>(env, IdentitySource.Environment);
        }

        var sidecarValue = _sidecar.Value?.Version;
        if (!string.IsNullOrEmpty(sidecarValue))
        {
            ValidateVersion(sidecarValue, IdentitySource.Sidecar);
            return new IdentityValue<string>(sidecarValue, IdentitySource.Sidecar);
        }

        return new IdentityValue<string>(_assemblyVersionAndCommit.Value.Version, IdentitySource.AssemblyFallback);
    }

    private IdentityValue<string> ResolveCommitCore()
    {
        if (TryGetEnv(CommitEnvVar, out var env))
        {
            ValidateCommit(env, IdentitySource.Environment);
            return new IdentityValue<string>(env, IdentitySource.Environment);
        }

        var sidecarValue = _sidecar.Value?.Commit;
        if (!string.IsNullOrEmpty(sidecarValue))
        {
            ValidateCommit(sidecarValue, IdentitySource.Sidecar);
            return new IdentityValue<string>(sidecarValue, IdentitySource.Sidecar);
        }

        return new IdentityValue<string>(_assemblyVersionAndCommit.Value.Commit, IdentitySource.AssemblyFallback);
    }

    private IdentityValue<string?> ResolveNuGetServiceIndexOverrideCore()
    {
        if (TryGetEnv(NuGetServiceIndexEnvVar, out var env))
        {
            ValidateNuGetServiceIndex(env, IdentitySource.Environment);
            return new IdentityValue<string?>(env, IdentitySource.Environment);
        }

        var sidecarValue = _sidecar.Value?.NuGetServiceIndexOverride;
        if (!string.IsNullOrEmpty(sidecarValue))
        {
            ValidateNuGetServiceIndex(sidecarValue, IdentitySource.Sidecar);
            return new IdentityValue<string?>(sidecarValue, IdentitySource.Sidecar);
        }

        // No assembly-baked override exists or could meaningfully exist. The
        // override is a runtime testing affordance, not a build-time property.
        return new IdentityValue<string?>(null, IdentitySource.TerminalDefault);
    }

    private IdentityValue<string?> ResolvePackagesDirectoryCore()
    {
        if (TryGetEnv(PackagesEnvVar, out var env))
        {
            return new IdentityValue<string?>(env, IdentitySource.Environment);
        }

        var sidecarValue = _sidecar.Value?.Packages;
        if (!string.IsNullOrEmpty(sidecarValue))
        {
            return new IdentityValue<string?>(sidecarValue, IdentitySource.Sidecar);
        }

        // Like the NuGet service-index override, this is a runtime testing
        // affordance with no assembly-baked equivalent, so the terminal
        // default is "no override".
        return new IdentityValue<string?>(null, IdentitySource.TerminalDefault);
    }

    // Identity overrides come from developer-controlled inputs — an ASPIRE_CLI_* env var or a
    // hand-authored .aspire-install.json. We validate the shape of the typed fields at resolve
    // time so a typo fails fast with a message naming the source, instead of silently producing
    // a bogus staging-feed name, an unrestorable NuGet.config URL, or a version that throws deep
    // inside SemVer parsing far from the cause. Channel is deliberately NOT validated here: bespoke
    // labels like "pr-17580" are legitimate overrides, and the assembly metadata reader already
    // validates the one channel input we control end-to-end. The packages directory is validated by
    // PackagingService when it is consumed (existence + unambiguous Aspire* versions). The
    // assembly-baked fallback is trusted and never routed through these checks.

    private static void ValidateVersion(string value, IdentitySource source)
    {
        // ASPIRE_CLI_VERSION mirrors AssemblyInformationalVersion, e.g. "13.4.3",
        // "13.5.0-preview.1.26311.9", or "13.4.0+abcdef0" (optional build metadata). Strict SemVer 2.0
        // is exactly that grammar and is the same parser the rest of the CLI uses for package versions
        // (see PackageChannel / PackageUpdateHelpers).
        if (!SemVersion.TryParse(value, SemVersionStyles.Strict, out _))
        {
            throw InvalidOverride(source, VersionEnvVar, "version", value,
                "a SemVer 2.0 version such as '13.4.3' or '13.5.0-preview.1.26311.9'");
        }
    }

    private static void ValidateCommit(string value, IdentitySource source)
    {
        // ASPIRE_CLI_COMMIT is the source revision carried in the "+<sha>" suffix of the informational
        // version. Its one behavioral use is deriving the staging feed name
        // darc-pub-microsoft-aspire-<sha8> (PackagingService takes the first 8 chars, lowercased), so
        // it must be hexadecimal AND at least 8 characters. A shorter value passes a naive hex check
        // but then yields a feed name one character short of that contract (a 7-char commit derives
        // ...-aspire-<7char>, which can never match a real darc feed), so restore fails far from the
        // typo. Accept 8 through 64 characters so git's abbreviated short SHAs and full SHA-1 (40) /
        // SHA-256 (64) revisions all validate.
        if (!IsHex(value, minLength: 8, maxLength: 64))
        {
            throw InvalidOverride(source, CommitEnvVar, "commit", value,
                "a hexadecimal commit SHA of 8 to 64 characters, e.g. 'abcdef01'");
        }
    }

    private static void ValidateNuGetServiceIndex(string value, IdentitySource source)
    {
        // The override is written verbatim into generated NuGet.config files as a v3 service index,
        // so it must be an absolute http(s) URL or NuGet restore fails with an opaque error far from
        // the typo.
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw InvalidOverride(source, NuGetServiceIndexEnvVar, "nugetServiceIndexOverride", value,
                "an absolute http(s) URL such as 'http://127.0.0.1:5400/v3/index.json'");
        }
    }

    private static bool IsHex(string value, int minLength, int maxLength)
    {
        if (value.Length < minLength || value.Length > maxLength)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!Uri.IsHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    private static InvalidOperationException InvalidOverride(IdentitySource source, string envVar, string sidecarField, string value, string expected)
    {
        // Name the exact input the developer set so the fix is obvious. Only env and sidecar values
        // flow here; the assembly fallback is never validated.
        var origin = source switch
        {
            IdentitySource.Environment => $"environment variable {envVar}",
            IdentitySource.Sidecar => $"'{sidecarField}' field in {InstallSidecarReader.SidecarFileName}",
            _ => "identity override",
        };

        return new InvalidOperationException($"The {origin} value '{value}' is not valid. Expected {expected}.");
    }

    private bool TryGetEnv(string name, out string value)
    {
        var raw = _envReader(name);
        if (string.IsNullOrEmpty(raw))
        {
            value = string.Empty;
            return false;
        }

        value = raw;
        return true;
    }

    private InstallSidecarInfo? LoadSidecar()
    {
        if (string.IsNullOrEmpty(_binaryDir))
        {
            return null;
        }

        return _sidecarReader.TryRead(_binaryDir) is InstallSidecarReadResult.Ok ok
            ? ok.Info
            : null;
    }

    private string LoadAssemblyChannel()
    {
        // Delegate to the existing assembly-only reader so we keep one canonical
        // shape validator for the AssemblyMetadata(AspireCliChannel, ...) value.
        // The reader throws on missing/invalid metadata, which is the right
        // behavior at the assembly layer; we catch here so a malformed stamp
        // does not nuke the whole resolver — we just fall through to the
        // terminal default (`local`).
        try
        {
            // Main refactored IdentityChannelReader to a Try pattern (see PR #17828).
            // Treat a `false` return the same as the old `InvalidOperationException`
            // path: fall through to the terminal default so a malformed stamp does
            // not nuke the whole resolver.
            return new IdentityChannelReader(_assembly).TryReadChannel(out var channel, out _)
                ? channel
                : string.Empty;
        }
        catch (InvalidOperationException)
        {
            return string.Empty;
        }
    }

    private (string Version, string Commit) LoadAssemblyVersionAndCommit()
    {
        // physical-binary-version-by-design (see docs/specs/cli-identity-sidecar.md):
        // this IS the assembly-fallback source for the identity system itself — the value used
        // when no ASPIRE_CLI_VERSION / sidecar override is present. It must read the assembly.
        // AssemblyInformationalVersion shape: "13.4.0-preview.1.25366.3+abcdef..."
        // The '+sha' suffix is optional (some build configurations omit it).
        var informational = AssemblyVersionHelper.GetInformationalVersion(_assembly);
        if (string.IsNullOrEmpty(informational))
        {
            return (string.Empty, string.Empty);
        }

        var plusIndex = informational.IndexOf('+');
        if (plusIndex < 0)
        {
            return (informational, string.Empty);
        }

        return (informational[..plusIndex], informational[(plusIndex + 1)..]);
    }
}
