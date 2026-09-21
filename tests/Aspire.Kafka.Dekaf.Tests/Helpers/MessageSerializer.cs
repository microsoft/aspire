// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Text;
using Dekaf.Serialization;

namespace Aspire.Kafka.Dekaf.Tests;

internal sealed record TestMessage(string Text);

internal sealed class MessageSerializer : ISerializer<TestMessage>, IDeserializer<TestMessage>
{
    public int SerializedCount { get; private set; }
    public int DeserializedCount { get; private set; }

    public void Serialize<TWriter>(TestMessage value, ref TWriter destination, SerializationContext context)
        where TWriter : IBufferWriter<byte>, allows ref struct
    {
        SerializedCount++;
        var count = Encoding.UTF8.GetByteCount(value.Text);
        var written = Encoding.UTF8.GetBytes(value.Text, destination.GetSpan(count));
        destination.Advance(written);
    }

    public TestMessage Deserialize(ReadOnlyMemory<byte> data, SerializationContext context)
    {
        DeserializedCount++;
        return new TestMessage(Encoding.UTF8.GetString(data.Span));
    }
}
