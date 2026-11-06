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
    /// Gets a value indicating whether the value couldn't be read, for example because the browser is disconnected or
    /// the stored value can't be deserialized. When <see langword="false"/> and <see cref="Success"/> is also
    /// <see langword="false"/>, nothing is stored.
    /// </summary>
    /// <remarks>
    /// Settings that are saved by reading the stored value and changing it use this to avoid replacing a value they
    /// couldn't read with one that's missing everything it contained.
    /// </remarks>
    public bool ReadFailed { get; init; }

    public StorageResult(bool success, TValue? value)
    {
        Success = success;
        Value = value;
    }
}
