// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Tray.Tests.Helpers;

internal sealed class TestTrayStartupRegistrationStore : ITrayStartupRegistrationStore
{
    public string? Value { get; set; }
    public int WriteCount { get; private set; }
    public Action? BeforeWrite { get; set; }

    public string? Read() => Value;

    public void Write(string? expected, string? value)
    {
        BeforeWrite?.Invoke();
        if (!string.Equals(Value, expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The startup registration changed outside Aspire.");
        }
        Value = value;
        WriteCount++;
    }
}
