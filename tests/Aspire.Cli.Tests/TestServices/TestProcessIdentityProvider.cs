// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Processes;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class TestProcessIdentityProvider : IProcessIdentityProvider
{
    public Func<int, long?> GetStartTime { get; set; } = _ => null;

    public long? GetStartTimeUnixMilliseconds(int processId) => GetStartTime(processId);
}
