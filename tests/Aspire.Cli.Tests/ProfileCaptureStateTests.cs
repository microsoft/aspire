// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Profiling;

namespace Aspire.Cli.Tests;

public class ProfileCaptureStateTests
{
    [Fact]
    public void MarkTransferred_SetsIsTransferred()
    {
        var state = new ProfileCaptureState();

        Assert.False(state.IsTransferred);

        state.MarkTransferred();

        Assert.True(state.IsTransferred);
    }
}
