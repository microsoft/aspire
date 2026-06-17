// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Aspire.Cli.Packaging;

namespace Aspire.Cli.Acquisition;

/// <summary>
/// Reads the acquisition channel that the running CLI assembly was built for.
/// </summary>
/// <remarks>
/// The channel is baked into the CLI assembly at build time as
/// <c>[AssemblyMetadata("AspireCliChannel", "&lt;value&gt;")]</c>. The value is
/// one of <c>stable</c>, <c>staging</c>, <c>daily</c>, <c>local</c> (the default
/// for developer builds with no <c>/p:AspireCliChannel=</c> override), or the
/// per-PR hive label <c>pr-&lt;N&gt;</c> for PR builds (where <c>&lt;N&gt;</c>
/// is the GitHub pull-request number, baked verbatim by CI; see
/// <c>.github/workflows/build-cli-native-archives.yml</c> and
/// <c>eng/pipelines/templates/build_sign_native.yml</c>).
/// </remarks>
internal interface IIdentityChannelReader
{
    /// <summary>
    /// Attempts to read the channel baked into the CLI assembly.
    /// </summary>
    /// <param name="channel">When this method returns <see langword="true"/>, contains the resolved channel value.</param>
    /// <param name="error">When this method returns <see langword="false"/>, contains the error message describing the failure.</param>
    /// <returns><see langword="true"/> if the channel was successfully read; otherwise, <see langword="false"/>.</returns>
    bool TryReadChannel([NotNullWhen(true)] out string? channel, [NotNullWhen(false)] out string? error);
}

/// <summary>
/// Default <see cref="IIdentityChannelReader"/> backed by an <see cref="Assembly"/>'s
/// <see cref="AssemblyMetadataAttribute"/> values.
/// </summary>
/// <remarks>
/// AOT-safe: enumerating <see cref="AssemblyMetadataAttribute"/> via
/// <see cref="CustomAttributeExtensions"/> over a sealed, build-time-known
/// attribute type is preserved by the trimmer / native compiler. No
/// reflection-based JSON, no dynamic type loading.
/// </remarks>
internal sealed class IdentityChannelReader : IIdentityChannelReader
{
    private const string ChannelMetadataKey = "AspireCliChannel";
    private const string PrChannelPrefix = "pr-";

    private readonly Assembly _assembly;
    private readonly Lazy<(bool Success, string? Channel, string? Error)> _cached;

    /// <summary>
    /// Initializes a new instance that reads metadata from the supplied
    /// <paramref name="assembly"/>. The assembly is required: defaulting to
    /// <see cref="Assembly.GetEntryAssembly()"/> is intentionally NOT supported
    /// because under <c>Microsoft.DotNet.RemoteExecutor</c> and other test
    /// hosts the entry assembly resolves to the host (not the CLI), which
    /// silently breaks <c>[AssemblyMetadata]</c> reads. Callers must pin the
    /// read explicitly: production passes <c>typeof(Program).Assembly</c>;
    /// tests pass a fake assembly carrying the desired metadata.
    /// </summary>
    /// <param name="assembly">
    /// The assembly to read <c>AspireCliChannel</c> metadata from. Must not be
    /// <see langword="null"/>.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="assembly"/> is <see langword="null"/>.
    /// </exception>
    public IdentityChannelReader(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        _assembly = assembly;
        _cached = new Lazy<(bool, string?, string?)>(() =>
        {
            var success = TryResolveChannel(_assembly, out var ch, out var err);
            return (success, ch, err);
        }, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <inheritdoc />
    public bool TryReadChannel([NotNullWhen(true)] out string? channel, [NotNullWhen(false)] out string? error)
    {
        var result = _cached.Value;
        channel = result.Channel;
        error = result.Error;
        return result.Success;
    }

    /// <summary>
    /// Attempts to resolve the channel from the specified assembly's metadata.
    /// </summary>
    /// <param name="assembly">The assembly to read <c>AspireCliChannel</c> metadata from.</param>
    /// <param name="channel">When this method returns <see langword="true"/>, contains the resolved channel value.</param>
    /// <param name="error">When this method returns <see langword="false"/>, contains the error message describing the failure.</param>
    /// <returns><see langword="true"/> if the channel was successfully resolved; otherwise, <see langword="false"/>.</returns>
    private static bool TryResolveChannel(Assembly assembly, [NotNullWhen(true)] out string? channel, [NotNullWhen(false)] out string? error)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var metadata = assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => string.Equals(a.Key, ChannelMetadataKey, StringComparison.Ordinal));

        if (metadata is null || string.IsNullOrEmpty(metadata.Value))
        {
            channel = null;
            error = $"Assembly metadata '{ChannelMetadataKey}' is missing or empty on '{assembly.GetName().Name}'. " +
                "The CLI must be built with /p:AspireCliChannel=<channel> (one of stable, staging, daily, local, or pr-<N>).";
            return false;
        }

        var value = metadata.Value;
        if (!IsValidChannel(value))
        {
            channel = null;
            error = $"Assembly metadata '{ChannelMetadataKey}' on '{assembly.GetName().Name}' has invalid value '{value}'. " +
                "Expected one of: stable, staging, daily, local, or pr-<N> where <N> is one or more ASCII digits.";
            return false;
        }

        channel = value;
        error = null;
        return true;
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="value"/> is a valid
    /// baked channel: one of the fixed identity strings, or <c>pr-&lt;N&gt;</c>
    /// with <c>&lt;N&gt;</c> one or more ASCII digits. Trailing/leading whitespace
    /// and any other shape (including the legacy literal <c>pr</c> without a
    /// suffix, which is how CI USED to bake PR builds) is rejected so misconfigured
    /// pipelines fail loudly here rather than producing a hive label of the
    /// literal <c>pr</c> and silently mis-routing packages.
    /// </summary>
    internal static bool IsValidChannel(string value)
    {
        if (value is PackageChannelNames.Stable or PackageChannelNames.Staging or PackageChannelNames.Daily or PackageChannelNames.Local)
        {
            return true;
        }

        if (!value.StartsWith(PrChannelPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        // Reject "pr-" with no suffix and "pr-<non-ASCII-digit-run>". MemoryExtensions
        // .ContainsAnyExceptInRange is AOT-safe and matches the ASCII-only contract
        // the build pipeline emits (never localized digits, never sign chars).
        var digits = value.AsSpan(PrChannelPrefix.Length);
        return !digits.IsEmpty && !digits.ContainsAnyExceptInRange('0', '9');
    }
}
