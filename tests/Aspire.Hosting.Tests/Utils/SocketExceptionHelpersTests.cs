// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Sockets;
using Aspire.Shared;

namespace Aspire.Hosting.Tests.Utils;

[Trait("Partition", "4")]
public sealed class SocketExceptionHelpersTests
{
    [Theory]
    [InlineData(SocketError.ConnectionReset, true)]
    [InlineData(SocketError.ConnectionAborted, false)]
    [InlineData(SocketError.ConnectionRefused, false)]
    [InlineData(SocketError.NetworkReset, false)]
    [InlineData(SocketError.Shutdown, false)]
    [InlineData(SocketError.TimedOut, false)]
    public void IsConnectionReset_OnlyMatchesResetSocketError(SocketError socketError, bool expected)
    {
        var exception = new IOException("Transport failure.", new SocketException((int)socketError));

        Assert.Equal(expected, SocketExceptionHelpers.IsConnectionReset(exception));
    }

    [Fact]
    public void IsConnectionReset_OnlyMatchesDirectIOExceptionWrapper()
    {
        var socketException = new SocketException((int)SocketError.ConnectionReset);

        Assert.False(SocketExceptionHelpers.IsConnectionReset(socketException));
        Assert.False(SocketExceptionHelpers.IsConnectionReset(new IOException("Unrelated I/O failure.")));
        Assert.False(SocketExceptionHelpers.IsConnectionReset(new InvalidOperationException("Unrelated failure.", socketException)));
        Assert.False(SocketExceptionHelpers.IsConnectionReset(new IOException("Outer failure.", new IOException("Inner failure.", socketException))));
    }
}
