// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.TypeSystem;

namespace Aspire.Shared.CodeGeneration;

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
    /// <remarks>
    /// When <paramref name="cancellationTokenIsSeparateParameter"/> is <see langword="true"/>
    /// a trailing cancellation token is rendered as its own parameter and is ignored when
    /// counting candidates; when <see langword="false"/> all optionals share one trailing
    /// variadic, so a coexisting cancellation token blocks flattening.
    /// </remarks>
    public static bool TryGetDirectOptionsParameter(
        IReadOnlyList<AtsParameterInfo> optionalParams,
        Func<AtsParameterInfo, bool> isCancellationToken,
        bool cancellationTokenIsSeparateParameter,
        [NotNullWhen(true)] out AtsParameterInfo? directOptionsParam)
    {
        directOptionsParam = null;

        IReadOnlyList<AtsParameterInfo> candidates = cancellationTokenIsSeparateParameter
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
