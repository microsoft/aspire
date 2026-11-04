// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Backchannel;
using StreamJsonRpc;

namespace Aspire.Cli.Tests.Backchannel;

public class BackchannelDisconnectHelpersTests
{
    [Fact]
    public void IsExpectedDisconnect_ClassifiesConnectionLossWithoutSwallowingOtherFailures()
    {
        var connectionLost = new ConnectionLostException();

        Assert.True(BackchannelDisconnectHelpers.IsExpectedDisconnect(connectionLost));
        Assert.True(BackchannelDisconnectHelpers.IsExpectedDisconnect(new ObjectDisposedException("backchannel")));
        Assert.True(BackchannelDisconnectHelpers.IsExpectedDisconnect(new OperationCanceledException("Connection closed", connectionLost)));
        Assert.False(BackchannelDisconnectHelpers.IsExpectedDisconnect(new OperationCanceledException()));
        Assert.False(BackchannelDisconnectHelpers.IsExpectedDisconnect(new InvalidOperationException("RPC failed")));
    }
}
