// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting;
using Microsoft.Extensions.Configuration;

namespace Aspire.Cli.Telemetry;

/// <summary>
/// Provides the Microsoft account snapshot reported by the Aspire VS Code extension.
/// </summary>
internal interface IVsCodeMicrosoftAccountProvider
{
    VsCodeMicrosoftAccountState GetInternalMicrosoftAccount();
}

internal sealed class EnvironmentVsCodeMicrosoftAccountProvider : IVsCodeMicrosoftAccountProvider
{
    // This process contract must match microsoftAccountProvider.ts in the Aspire VS Code extension.
    internal const string StateEnvironmentVariable = "ASPIRE_EXTENSION_MICROSOFT_ACCOUNT_STATE";
    internal const string AliasEnvironmentVariable = "ASPIRE_EXTENSION_MICROSOFT_ACCOUNT_ALIAS";
    internal const string InternalState = "internal";
    internal const string NotInternalState = "not_internal";
    internal const string RefreshingState = "refreshing";
    internal const string UnavailableState = "unavailable";

    private readonly VsCodeMicrosoftAccountState _state;

    private EnvironmentVsCodeMicrosoftAccountProvider(VsCodeMicrosoftAccountState state)
    {
        _state = state;
    }

    public VsCodeMicrosoftAccountState GetInternalMicrosoftAccount() => _state;

    internal static EnvironmentVsCodeMicrosoftAccountProvider CaptureAndClear(
        IConfiguration configuration,
        Action<string>? clearEnvironmentVariable = null)
    {
        var stateValue = configuration[StateEnvironmentVariable];
        var aliasValue = configuration[AliasEnvironmentVariable];
        var isExtensionInvocation =
            !string.IsNullOrEmpty(configuration[KnownConfigNames.ExtensionEndpoint]) &&
            !string.IsNullOrEmpty(configuration[KnownConfigNames.ExtensionToken]) &&
            !string.IsNullOrEmpty(configuration[KnownConfigNames.ExtensionCert]);
        var state = isExtensionInvocation
            ? stateValue is null && aliasValue is null
                // Older extensions have a valid backchannel but no account contract. Avoid caching
                // a false negative before a later invocation supplies the live snapshot.
                ? VsCodeMicrosoftAccountState.Suppressed
                : Parse(stateValue, aliasValue)
            : ParseUntrustedNonPositiveState(stateValue, aliasValue);

        configuration[StateEnvironmentVariable] = null;
        configuration[AliasEnvironmentVariable] = null;

        var clear = clearEnvironmentVariable ?? ClearProcessEnvironmentVariable;
        clear(StateEnvironmentVariable);
        clear(AliasEnvironmentVariable);

        return new(state);
    }

    private static VsCodeMicrosoftAccountState Parse(string? state, string? alias)
    {
        // The extension emits one of:
        //   STATE=internal      ALIAS=<normalized-alias>
        //   STATE=not_internal ALIAS absent
        //   STATE=refreshing   ALIAS absent
        //   STATE=unavailable  ALIAS absent
        // A missing pair identifies an older extension. Any other combination is treated as
        // unavailable so malformed input cannot invalidate a valid cache as an explicit sign-out.
        if (state is null && alias is null)
        {
            return VsCodeMicrosoftAccountState.Missing;
        }

        var normalizedAlias = NormalizeAlias(alias);
        if (state?.Equals(InternalState, StringComparison.OrdinalIgnoreCase) == true &&
            normalizedAlias is not null)
        {
            return VsCodeMicrosoftAccountState.Available(normalizedAlias);
        }

        if (state?.Equals(NotInternalState, StringComparison.OrdinalIgnoreCase) == true &&
            alias is null)
        {
            return VsCodeMicrosoftAccountState.Available(alias: null);
        }

        if (state?.Equals(UnavailableState, StringComparison.OrdinalIgnoreCase) == true &&
            alias is null)
        {
            return VsCodeMicrosoftAccountState.Unavailable;
        }

        if (state?.Equals(RefreshingState, StringComparison.OrdinalIgnoreCase) == true &&
            alias is null)
        {
            return VsCodeMicrosoftAccountState.Refreshing;
        }

        return VsCodeMicrosoftAccountState.Unavailable;
    }

    private static VsCodeMicrosoftAccountState ParseUntrustedNonPositiveState(string? state, string? alias)
    {
        if (alias is not null)
        {
            return VsCodeMicrosoftAccountState.Missing;
        }

        if (state?.Equals(NotInternalState, StringComparison.OrdinalIgnoreCase) == true)
        {
            return VsCodeMicrosoftAccountState.Available(alias: null);
        }

        return state is not null &&
            (state.Equals(UnavailableState, StringComparison.OrdinalIgnoreCase) ||
             state.Equals(RefreshingState, StringComparison.OrdinalIgnoreCase))
            ? VsCodeMicrosoftAccountState.Suppressed
            : VsCodeMicrosoftAccountState.Missing;
    }

    private static string? NormalizeAlias(string? alias)
    {
        if (string.IsNullOrWhiteSpace(alias))
        {
            return null;
        }

        var normalized = alias.Trim();
        return normalized.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')
            ? normalized.ToLowerInvariant()
            : null;
    }

    private static void ClearProcessEnvironmentVariable(string name)
    {
        foreach (System.Collections.DictionaryEntry variable in Environment.GetEnvironmentVariables())
        {
            if (((string)variable.Key).Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                Environment.SetEnvironmentVariable((string)variable.Key, null);
            }
        }
    }
}

internal sealed record VsCodeMicrosoftAccountState(VsCodeMicrosoftAccountStateKind Kind, string? Alias)
{
    internal static VsCodeMicrosoftAccountState Missing { get; } = new(VsCodeMicrosoftAccountStateKind.Missing, Alias: null);

    internal static VsCodeMicrosoftAccountState Unavailable { get; } = new(VsCodeMicrosoftAccountStateKind.Unavailable, Alias: null);

    internal static VsCodeMicrosoftAccountState Suppressed { get; } = new(VsCodeMicrosoftAccountStateKind.Suppressed, Alias: null);

    internal static VsCodeMicrosoftAccountState Refreshing { get; } = new(VsCodeMicrosoftAccountStateKind.Refreshing, Alias: null);

    internal static VsCodeMicrosoftAccountState Available(string? alias) => new(VsCodeMicrosoftAccountStateKind.Available, alias);

    internal bool IsAvailable => Kind == VsCodeMicrosoftAccountStateKind.Available;

    internal bool IsUnavailable => Kind == VsCodeMicrosoftAccountStateKind.Unavailable;

    internal bool InvalidatesCache => Kind == VsCodeMicrosoftAccountStateKind.Refreshing;

    internal bool PreventsNegativeCache => Kind is VsCodeMicrosoftAccountStateKind.Refreshing or VsCodeMicrosoftAccountStateKind.Suppressed;
}

internal enum VsCodeMicrosoftAccountStateKind
{
    Missing,
    Available,
    Unavailable,
    Suppressed,
    Refreshing
}
