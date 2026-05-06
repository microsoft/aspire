// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Reflection;

namespace Aspire.Cli.Acquisition;

/// <summary>
/// Reads the acquisition channel that the running CLI assembly was built for.
/// </summary>
/// <remarks>
/// The channel is baked into the CLI assembly at build time as
/// <c>[AssemblyMetadata("AspireCliChannel", "&lt;value&gt;")]</c>. The value is
/// one of <c>stable</c>, <c>staging</c>, <c>daily</c>, or <c>pr</c>.
/// </remarks>
internal interface IIdentityChannelReader
{
    /// <summary>
    /// Returns the channel baked into the CLI assembly.
    /// </summary>
    /// <returns>One of <c>stable</c>, <c>staging</c>, <c>daily</c>, or <c>pr</c>.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the <c>AspireCliChannel</c> assembly metadata is missing or empty.
    /// </exception>
    string ReadChannel();
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
    private const string PrChannelMarker = "-pr";

    private readonly Assembly? _assembly;

    /// <summary>
    /// Initializes a new instance that reads metadata from the supplied
    /// <paramref name="assembly"/>, defaulting to <see cref="Assembly.GetEntryAssembly()"/>
    /// when <see langword="null"/> (the production case).
    /// </summary>
    /// <param name="assembly">
    /// The assembly to read metadata from. Production callers (DI) pass
    /// <see langword="null"/>; tests pass a fake assembly.
    /// </param>
    public IdentityChannelReader(Assembly? assembly = null)
    {
        _assembly = assembly ?? Assembly.GetEntryAssembly();
    }

    /// <inheritdoc />
    public string ReadChannel()
    {
        if (_assembly is null)
        {
            throw new InvalidOperationException(
                $"Could not determine the entry assembly to read '{ChannelMetadataKey}' metadata from.");
        }

        var metadata = _assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => string.Equals(a.Key, ChannelMetadataKey, StringComparison.Ordinal));

        if (metadata is null || string.IsNullOrEmpty(metadata.Value))
        {
            throw new InvalidOperationException(
                $"Assembly metadata '{ChannelMetadataKey}' is missing or empty on '{_assembly.GetName().Name}'. " +
                "The CLI must be built with /p:AspireCliChannel=<channel> (one of stable, staging, daily, pr).");
        }

        return metadata.Value;
    }

    /// <summary>
    /// Parses the PR number out of an <see cref="AssemblyInformationalVersionAttribute"/>
    /// value of the form <c>0.0.0-pr&lt;N&gt;.&lt;sha&gt;</c>.
    /// </summary>
    /// <param name="informationalVersion">
    /// The informational version string. Typically obtained from
    /// <c>typeof(Program).Assembly.GetCustomAttribute&lt;AssemblyInformationalVersionAttribute&gt;()?.InformationalVersion</c>.
    /// </param>
    /// <returns>
    /// The PR number when <paramref name="informationalVersion"/> contains the
    /// <c>-pr&lt;digits&gt;</c> marker; otherwise <see langword="null"/>.
    /// </returns>
    internal static int? ParsePrNumber(string? informationalVersion)
    {
        if (string.IsNullOrEmpty(informationalVersion))
        {
            return null;
        }

        var idx = informationalVersion.IndexOf(PrChannelMarker, StringComparison.Ordinal);
        if (idx < 0)
        {
            return null;
        }

        var start = idx + PrChannelMarker.Length;
        var end = start;
        while (end < informationalVersion.Length && char.IsAsciiDigit(informationalVersion[end]))
        {
            end++;
        }

        if (end == start)
        {
            return null;
        }

        var span = informationalVersion.AsSpan(start, end - start);
        return int.TryParse(span, NumberStyles.None, CultureInfo.InvariantCulture, out var prNumber)
            ? prNumber
            : null;
    }
}
