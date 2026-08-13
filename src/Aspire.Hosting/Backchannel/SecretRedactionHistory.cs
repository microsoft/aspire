// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Backchannel;

/// <summary>
/// AppHost-scoped, add-only history of the secret parameters and resolved secret values that any
/// <c>aspire describe</c>/<c>watch</c> backchannel connection has ever observed, so they stay redacted from data
/// sent to clients (https://github.com/microsoft/aspire/issues/19241).
/// </summary>
/// <remarks>
/// Registered as a singleton for the lifetime of the AppHost and shared by every
/// <see cref="AuxiliaryBackchannelRpcTarget"/> (one is created per connection). The redaction set must outlive an
/// individual connection: a resource's secret can change while the app runs, and a snapshot carrying an older value
/// can be emitted to a client that connected <em>after</em> the change. If each connection tracked history on its
/// own, a freshly connected client's target would start empty and leak that older value, so history is accumulated
/// once per AppHost instead.
/// <para>
/// Both sets only ever grow:
/// </para>
/// <list type="bullet">
/// <item>
/// The parameter set grows because when DCP restarts a resource it forgets and re-evaluates the resource's callbacks
/// (<c>DcpExecutor.ForgetCachedCallbackResults</c>), which can swap which secret a resource references. A
/// still-in-flight snapshot from the prior incarnation can carry the old secret, so we keep trying to resolve every
/// secret parameter ever observed rather than only the current pass's set.
/// </item>
/// <item>
/// The value set grows because a parameter's resolved value can be replaced in place: the runtime "Set parameter"
/// path swaps a completed <see cref="ParameterResource.WaitForValueTcs"/> for a new one
/// (<c>ParameterProcessor.SetParameterValue</c>), so re-resolving a retained parameter later yields only the new
/// value. An already-published or still-current snapshot can still carry the previous value, so we must keep
/// redacting every secret string ever resolved or that old value would be emitted in plaintext.
/// </item>
/// </list>
/// <para>
/// This narrows but does not fully close a cold-start residual: a value assigned and then reassigned before any
/// connection ever observed it is not in the history. In practice the always-on dashboard/CLI watch keeps a
/// connection open from startup, so the history is populated continuously.
/// </para>
/// </remarks>
internal sealed class SecretRedactionHistory
{
    // Collected by reference: parameter resources referenced by annotations are not registered in the model and so
    // are not subject to its unique-name constraint, and distinct same-named secrets must be preserved.
    private readonly HashSet<ParameterResource> _parameters = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<string> _values = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    /// <summary>
    /// Merges <paramref name="parameters"/> into the history and returns a snapshot of every secret parameter seen so
    /// far. Pass an empty sequence to read the current snapshot without adding.
    /// </summary>
    public IReadOnlyList<ParameterResource> AddParametersAndSnapshot(IEnumerable<ParameterResource> parameters)
    {
        lock (_lock)
        {
            _parameters.UnionWith(parameters);
            return [.. _parameters];
        }
    }

    /// <summary>
    /// Merges <paramref name="values"/> into the history and returns a fresh snapshot of every resolved secret value
    /// seen so far, for membership testing while redacting.
    /// </summary>
    public HashSet<string> AddValuesAndSnapshot(IEnumerable<string> values)
    {
        lock (_lock)
        {
            _values.UnionWith(values);
            return new HashSet<string>(_values, StringComparer.Ordinal);
        }
    }
}
