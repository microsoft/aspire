// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Terminals;
using Microsoft.Extensions.Logging.Abstractions;

#pragma warning disable ASPIRETERMINAL002 // Test consumer of the experimental AppHost terminal API.

namespace Aspire.Hosting.Utils;

/// <summary>
/// Creates <see cref="TerminalService"/> instances for tests.
/// </summary>
/// <remarks>
/// <para>
/// There is no test double here on purpose. <see cref="TerminalService"/> is a registry plus a change-notification
/// fan-out; constructing one is cheap and it never starts a Hex1b workload until something attaches to a terminal.
/// Tests that only need the service to exist as a constructor dependency therefore get better coverage from the real
/// type than from a fake.
/// </para>
/// </remarks>
internal static class TestTerminalService
{
    public static TerminalService Create()
        => new(NullLogger<TerminalService>.Instance);
}
