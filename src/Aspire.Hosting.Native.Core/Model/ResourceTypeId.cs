// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Native.Model;

/// <summary>Identifies a declared integration resource kind without requiring a CLR type.</summary>
internal sealed record ResourceTypeId
{
    public string Value { get; }

    public ResourceTypeId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var separator = value.IndexOf('/');
        if (separator <= 0 || separator == value.Length - 1 ||
            value.IndexOf('/', separator + 1) >= 0 || value.Any(char.IsWhiteSpace) || value.Any(char.IsControl))
        {
            throw new ArgumentException("Resource type identities must have the form 'namespace/type'.", nameof(value));
        }
        Value = value;
    }

    public override string ToString() => Value;
}
