// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Profiling;

/// <summary>
/// Tracks whether profile capture export was transferred to a delegated CLI process.
/// </summary>
internal sealed class ProfileCaptureState
{
    private int _isTransferred;

    public bool IsTransferred => Volatile.Read(ref _isTransferred) != 0;

    public void MarkTransferred()
    {
        Interlocked.Exchange(ref _isTransferred, 1);
    }
}
