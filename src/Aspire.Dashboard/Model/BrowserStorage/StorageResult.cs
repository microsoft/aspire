// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Dashboard.Model.BrowserStorage;

public readonly struct StorageResult<TValue>
{
    [MemberNotNullWhen(true, nameof(Value))]
    public bool Success { get; }

    public TValue? Value { get; }

    /// <summary>
    /// Gets a value indicating whether browser storage couldn't be read, for example because the circuit is
    /// disconnected. Reading again later can succeed. When neither this nor <see cref="InvalidValue"/> is set and
    /// <see cref="Success"/> is <see langword="false"/>, nothing is stored.
    /// </summary>
    /// <remarks>
    /// Settings that are saved by reading the stored value and changing it use this to avoid replacing a value they
    /// couldn't read with one that's missing everything it contained.
    /// </remarks>
    public bool ReadFailed { get; init; }

    /// <summary>
    /// Gets a value indicating whether a value is stored but can't be deserialized, for example because it was edited
    /// or saved by another version of the dashboard. Reading again returns the same result.
    /// </summary>
    public bool InvalidValue { get; init; }

    public StorageResult(bool success, TValue? value)
    {
        Success = success;
        Value = value;
    }
}
