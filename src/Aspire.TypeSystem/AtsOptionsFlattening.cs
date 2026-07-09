// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.TypeSystem;

/// <summary>
/// Controls how a coexisting cancellation token affects options-flattening.
/// </summary>
internal enum CoexistingCancellationTokenPolicy
{
    /// <summary>
    /// The cancellation token is emitted as its own trailing parameter, so it is ignored when
    /// deciding whether a lone <c>options</c> DTO can be flattened (e.g. TypeScript).
    /// </summary>
    ThreadSeparately,

    /// <summary>
    /// The language models all optionals as a single trailing variadic and permits only one, so a
    /// coexisting cancellation token blocks flattening (e.g. Go).
    /// </summary>
    BlockFlattening,
}

/// <summary>
/// Shared decision for flattening a single optional <c>options</c> DTO so callers pass the DTO
/// directly instead of through a generated wrapper. Used by the polyglot code generators.
/// </summary>
internal static class AtsOptionsFlattening
{
    /// <summary>
    /// Determines whether <paramref name="optionalParams"/> is exactly one DTO parameter named
    /// <c>options</c> (no callback, no cancellation token) that can be flattened. The caller
    /// supplies <paramref name="isCancellationToken"/> because languages differ on what type ids
    /// count as a cancellation token.
    /// </summary>
    public static bool TryGetDirectOptionsParameter(
        IReadOnlyList<AtsParameterInfo> optionalParams,
        Func<AtsParameterInfo, bool> isCancellationToken,
        CoexistingCancellationTokenPolicy cancellationTokenPolicy,
        [NotNullWhen(true)] out AtsParameterInfo? directOptionsParam)
    {
        directOptionsParam = null;

        // ThreadSeparately languages drop coexisting cancellation tokens before counting because
        // they render them as their own trailing parameter; BlockFlattening languages cannot.
        IReadOnlyList<AtsParameterInfo> candidates = cancellationTokenPolicy == CoexistingCancellationTokenPolicy.ThreadSeparately
            ? optionalParams.Where(p => !isCancellationToken(p)).ToList()
            : optionalParams;

        if (candidates.Count != 1)
        {
            return false;
        }

        var candidate = candidates[0];
        if (candidate.IsCallback || isCancellationToken(candidate))
        {
            return false;
        }
        if (!string.Equals(candidate.Name, "options", StringComparison.Ordinal))
        {
            return false;
        }
        if (candidate.Type?.Category != AtsTypeCategory.Dto)
        {
            return false;
        }

        directOptionsParam = candidate;
        return true;
    }
}
