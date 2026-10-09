// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Processes;

internal static class ProcessEnvironment
{
    // OrdinalIgnoreCase mirrors ProcessStartInfo's behavior on Windows (env vars are
    // case-insensitive). Using it on all platforms is slightly less strict than the
    // Unix kernel (which treats env names as bytes) but it prevents the trap of
    // accidentally having both "Path" and "PATH" entries.
    public static StringComparer Comparer => StringComparer.OrdinalIgnoreCase;
}