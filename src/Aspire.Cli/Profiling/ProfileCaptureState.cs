// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Profiling;

/// <summary>
/// Tracks whether command execution was transferred to a delegated CLI process.
/// </summary>
internal sealed class ProfileCaptureState
{
    internal bool IsTransferred { get; private set; }

    internal void MarkTransferred() => IsTransferred = true;
}
