// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers.Binary;
using System.Text.Json;
using Aspire.Shared.DotnetWatch;

namespace Aspire.Cli.Tests.DotnetWatch;

public class DotnetWatchAppHostProtocolTests
{
    [Theory]
    [InlineData(DotnetWatchAppHostProtocol.ModelInvalidated)]
    [InlineData(DotnetWatchAppHostProtocol.HostStarted)]
    public async Task RoundTripRetainsVersionSessionAndProcessInstance(int kind)
    {
        var notification = new DotnetWatchAppHostNotification
        {
            Version = DotnetWatchAppHostProtocol.Version,
            Session = "0123456789abcdef0123456789abcdef",
            Kind = kind,
            Instance = "12345:67890"
        };
        using var stream = new MemoryStream();
        await DotnetWatchAppHostProtocol.WriteAsync(stream, notification, TestContext.Current.CancellationToken);
        stream.Position = 0;
        var received = await DotnetWatchAppHostProtocol.ReadAsync(stream, TestContext.Current.CancellationToken);

        Assert.Equal(notification.Version, received.Version);
        Assert.Equal(notification.Session, received.Session);
        Assert.Equal(notification.Kind, received.Kind);
        Assert.Equal(notification.Instance, received.Instance);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(DotnetWatchAppHostProtocol.MaximumMessageBytes + 1)]
    [InlineData(int.MaxValue)]
    public async Task InvalidLengthsAreRejectedBeforeReadingOrAllocatingThePayload(int length)
    {
        var frame = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(frame, length);
        using var stream = new MemoryStream(frame);

        await Assert.ThrowsAsync<InvalidDataException>(() => DotnetWatchAppHostProtocol.ReadAsync(stream, TestContext.Current.CancellationToken));
        Assert.Equal(sizeof(int), stream.Position);
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"Version":1,"Session":"0123456789abcdef0123456789abcdef","Kind":0}""")]
    [InlineData("""{"Version":1,"Session":"0123456789abcdef0123456789abcdef","Instance":"123:456"}""")]
    [InlineData("""{"Version":1,"Session":"0123456789abcdef0123456789abcdef","Kind":0,"Instance":"123:456","Unexpected":true}""")]
    public async Task RequiredFieldsAndUnknownFieldsAreValidatedByTheGeneratedSerializer(string json)
    {
        using var stream = new MemoryStream(DotnetWatchAppHostTestChannel.Frame(json));

        await Assert.ThrowsAsync<JsonException>(() => DotnetWatchAppHostProtocol.ReadAsync(stream, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(2, "0123456789abcdef0123456789abcdef", 0, "123:456")]
    [InlineData(1, "stale-or-malformed-session", 0, "123:456")]
    [InlineData(1, "0123456789abcdef0123456789abcdef", 2, "123:456")]
    [InlineData(1, "0123456789abcdef0123456789abcdef", 0, "123")]
    [InlineData(1, "0123456789abcdef0123456789abcdef", 0, "0:456")]
    [InlineData(1, "0123456789abcdef0123456789abcdef", 0, "123:0")]
    [InlineData(1, "0123456789abcdef0123456789abcdef", 0, "123:456:789")]
    public async Task InvalidVersionKindSessionOrInstanceIsRejected(int version, string session, int kind, string instance)
    {
        var json = $$"""{"Version":{{version}},"Session":"{{session}}","Kind":{{kind}},"Instance":"{{instance}}"}""";
        using var stream = new MemoryStream(DotnetWatchAppHostTestChannel.Frame(json));

        await Assert.ThrowsAsync<InvalidDataException>(() => DotnetWatchAppHostProtocol.ReadAsync(stream, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DuplicateFieldsAreNotAcceptedAsAnIdentity()
    {
        using var stream = new MemoryStream(DotnetWatchAppHostTestChannel.Frame(
            """{"Version":1,"Session":"0123456789abcdef0123456789abcdef","Session":"fedcba9876543210fedcba9876543210","Kind":0,"Instance":"123:456"}"""));

        await Assert.ThrowsAsync<InvalidDataException>(() => DotnetWatchAppHostProtocol.ReadAsync(stream, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TruncatedRecordsAreNotTreatedAsNotifications()
    {
        var frame = DotnetWatchAppHostTestChannel.Frame(
            """{"Version":1,"Session":"0123456789abcdef0123456789abcdef","Kind":0,"Instance":"123:456"}""");
        using var stream = new MemoryStream(frame[..^1]);

        await Assert.ThrowsAsync<EndOfStreamException>(() => DotnetWatchAppHostProtocol.ReadAsync(stream, TestContext.Current.CancellationToken));
    }
}
