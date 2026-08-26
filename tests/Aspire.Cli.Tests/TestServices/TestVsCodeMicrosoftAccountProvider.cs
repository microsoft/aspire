// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Telemetry;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class TestVsCodeMicrosoftAccountProvider(bool isAvailable = false, string? alias = null) : IVsCodeMicrosoftAccountProvider
{
    public bool IsAvailable { get; set; } = isAvailable;
    public string? Alias { get; set; } = alias;
    public Func<CancellationToken, Task<VsCodeMicrosoftAccountState>>? GetInternalMicrosoftAccountAsyncCallback { get; set; }

    public Task<VsCodeMicrosoftAccountState> GetInternalMicrosoftAccountAsync(CancellationToken cancellationToken)
    {
        return GetInternalMicrosoftAccountAsyncCallback?.Invoke(cancellationToken)
            ?? Task.FromResult(IsAvailable
                ? VsCodeMicrosoftAccountState.Available(Alias)
                : VsCodeMicrosoftAccountState.Unavailable);
    }
}
