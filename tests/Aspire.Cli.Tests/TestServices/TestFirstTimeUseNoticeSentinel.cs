// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Utils;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class TestFirstTimeUseNoticeSentinel : IFirstTimeUseNoticeSentinel
{
    public bool SentinelExists { get; set; }
    public bool WasCreated { get; private set; }

    public bool Exists() => SentinelExists;

    public void CreateIfNotExists()
    {
        if (!SentinelExists)
        {
            SentinelExists = true;
            WasCreated = true;
        }
    }
}
