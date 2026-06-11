// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.TestUtilities;

/// <summary>
/// Thread-safe helper to ensure Verify is only initialized once across all test assemblies.
/// This is needed because TestModuleInitializer.cs is compiled into multiple test assemblies,
/// and DerivePathInfo can only be called before any Verify test has run.
/// </summary>
public static class VerifyInitializer
{
    private static int s_initialized;

    /// <summary>
    /// Attempts to mark Verify as initialized. Returns true if this is the first caller,
    /// false if another assembly already initialized it.
    /// </summary>
    public static bool TryInitialize()
    {
        return Interlocked.Exchange(ref s_initialized, 1) == 0;
    }
}
