// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TerminalHost;
using Aspire.Shared;

BundleVersionLease? acquiredBundleLease;
try
{
    acquiredBundleLease = BundleVersionLease.TryAcquireFromEnvironment("terminalhost");
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
{
    Console.Error.WriteLine($"Failed to acquire Aspire bundle lease: {ex.Message}");
    return 1;
}

using var bundleLease = acquiredBundleLease;

return await TerminalHostProcessRunner.RunAsync(args).ConfigureAwait(false);
