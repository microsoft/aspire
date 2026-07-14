// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Radius.Secrets;

/// <summary>
/// One inline <c>data</c> key binding: an Aspire secret <see cref="ParameterResource"/>
/// plus an optional explicit per-key <c>encoding</c> (<see langword="null"/> ⇒ the store
/// type's default, applied at emission).
/// </summary>
internal sealed record RadiusSecretKeyBinding(ParameterResource Parameter, string? Encoding);

/// <summary>
/// The single population mode of a <see cref="RadiusSecretStoreResource"/>. Exactly one
/// of the three modes must be declared; the fail-fast validation gate rejects zero or
/// more than one (<c>ASPIRERADIUS041</c>). This type accumulates whatever the fluent
/// methods set so the gate can count the declared modes.
/// </summary>
internal sealed class RadiusSecretStorePopulation
{
    /// <summary><see langword="true"/> when <c>WithData(...)</c> was called (inline, Radius-created).</summary>
    public bool HasInlineData { get; set; }

    /// <summary>Inline <c>data</c> key → secret-parameter binding (ordinal-keyed).</summary>
    public Dictionary<string, RadiusSecretKeyBinding> Data { get; } = new(StringComparer.Ordinal);

    /// <summary><see langword="true"/> when <c>WithExistingSecret(...)</c> was called.</summary>
    public bool HasExistingSecret { get; set; }

    /// <summary><see langword="true"/> when <c>WithSealedSecret(...)</c> was called.</summary>
    public bool HasSealedSecret { get; set; }

    /// <summary>
    /// The <c>&lt;namespace&gt;/&lt;name&gt;</c> or bare <c>&lt;name&gt;</c> reference for the
    /// existing/sealed modes; <see langword="null"/> for inline.
    /// </summary>
    public string? ResourceReference { get; set; }

    /// <summary>The <c>SealedSecret</c> manifest path for the sealed mode; <see langword="null"/> otherwise.</summary>
    public string? SealedManifestPath { get; set; }

    /// <summary>The keys to expose from the referenced <c>Secret</c> (existing/sealed modes).</summary>
    public List<string> Keys { get; } = [];

    /// <summary>The number of declared population modes (must be exactly 1 — else <c>ASPIRERADIUS041</c>).</summary>
    public int DeclaredModeCount =>
        (HasInlineData ? 1 : 0) + (HasExistingSecret ? 1 : 0) + (HasSealedSecret ? 1 : 0);

    /// <summary><see langword="true"/> when the store references a cluster <c>Secret</c> (existing or sealed).</summary>
    public bool IsSecretReference => HasExistingSecret || HasSealedSecret;
}
