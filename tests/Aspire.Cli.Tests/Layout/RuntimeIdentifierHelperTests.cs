// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;
using Aspire.Cli.Layout;

namespace Aspire.Cli.Tests;

public class RuntimeIdentifierHelperTests
{
    [Fact]
    public void GetCurrent_ReturnsTheRuntimeRid()
    {
        Assert.Equal(RuntimeInformation.RuntimeIdentifier, RuntimeIdentifierHelper.GetCurrent());
    }
}
