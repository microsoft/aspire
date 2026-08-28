// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Telemetry;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class TestVsCodeMicrosoftAccountProvider(bool isAvailable = false, string? alias = null) : IVsCodeMicrosoftAccountProvider
{
    public bool IsAvailable { get; set; } = isAvailable;
    public string? Alias { get; set; } = alias;
    public bool IsUnavailable { get; set; }
    public bool IsRefreshing { get; set; }
    public bool IsSuppressed { get; set; }

    public VsCodeMicrosoftAccountState GetInternalMicrosoftAccount()
    {
        if (IsRefreshing)
        {
            return VsCodeMicrosoftAccountState.Refreshing;
        }

        if (IsSuppressed)
        {
            return VsCodeMicrosoftAccountState.Suppressed;
        }

        if (IsUnavailable)
        {
            return VsCodeMicrosoftAccountState.Unavailable;
        }

        return IsAvailable
            ? VsCodeMicrosoftAccountState.Available(Alias)
            : VsCodeMicrosoftAccountState.Missing;
    }
}
