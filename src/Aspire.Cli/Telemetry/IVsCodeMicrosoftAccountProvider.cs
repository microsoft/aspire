// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Telemetry;

/// <summary>
/// Provides the Microsoft account alias reported by the Aspire VS Code extension.
/// </summary>
internal interface IVsCodeMicrosoftAccountProvider
{
    Task<VsCodeMicrosoftAccountState> GetInternalMicrosoftAccountAsync(CancellationToken cancellationToken);
}

internal sealed class NullVsCodeMicrosoftAccountProvider : IVsCodeMicrosoftAccountProvider
{
    public Task<VsCodeMicrosoftAccountState> GetInternalMicrosoftAccountAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(VsCodeMicrosoftAccountState.Unavailable);
    }
}

internal sealed record VsCodeMicrosoftAccountState(bool IsAvailable, string? Alias)
{
    internal static VsCodeMicrosoftAccountState Unavailable { get; } = new(IsAvailable: false, Alias: null);

    internal static VsCodeMicrosoftAccountState Available(string? alias) => new(IsAvailable: true, alias);
}
