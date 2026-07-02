// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dcp.Model;

namespace Aspire.Hosting.Dcp;

internal static class DcpStateMapper
{
    public static string? NormalizeExecutableState(string? state)
    {
        // DCP reports "Terminated" when it intentionally kills an executable during scale-down
        // or object deletion. Aspire doesn't expose that as a separate public/dashboard state;
        // "Exited" is the closest existing terminal state and still preserves unsuccessful
        // WaitForCompletion behavior when DCP does not report a matching exit code.
        return string.Equals(state, ExecutableState.Terminated, StringComparisons.ResourceState)
            ? KnownResourceStates.Exited
            : state;
    }

    public static bool IsExecutableTerminated(string? state)
    {
        return string.Equals(state, ExecutableState.Terminated, StringComparisons.ResourceState);
    }
}
