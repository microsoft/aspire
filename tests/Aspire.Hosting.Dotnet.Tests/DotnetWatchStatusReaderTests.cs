// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Aspire.Hosting.Dotnet.Watch;

namespace Aspire.Hosting.Dotnet.Tests;

public class DotnetWatchStatusReaderTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task EnforcesExactRecordByteLimit(int extra, bool valid)
    {
        const string payload = """{"type":"build_complete","projects":[],"success":true}""";
        var record = payload.PadRight(DotnetWatchStatusReader.MaximumRecordBytes + extra) + "\n";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(record));
        var read = ReadAllAsync(stream);
        if (valid)
        {
            var status = Assert.Single(await read);
            Assert.Equal(DotnetWatchStatusTypes.BuildComplete, status.Type);
            Assert.True(status.Success);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => read);
        }
    }

    [Theory]
    [InlineData("""{"type":"process_started","projects":[]}""")]
    [InlineData("""{"type":"process_exited","projects":["/a.csproj"]}""")]
    [InlineData("""{"type":"build_complete","projects":[]}""")]
    [InlineData("""{"type":"building","projects":[null]}""")]
    [InlineData("""{"type":"unknown","projects":[]}""")]
    public async Task RejectsIncompleteStatusContracts(string record)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(record + "\n"));
        await Assert.ThrowsAsync<InvalidDataException>(() => ReadAllAsync(stream));
    }

    [Fact]
    public async Task RejectsPartialRecordAtEof()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("""{"type":"building","projects":[]}"""));
        await Assert.ThrowsAsync<InvalidDataException>(() => ReadAllAsync(stream));
    }

    [Fact]
    public async Task PreservesEscapedNewlinesAndMultipleRecords()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(
            """{"type":"build_complete","projects":["/a.csproj"],"success":false,"error":"first\nsecond"}""" + "\n" +
            """{"type":"process_started","projects":["/a.csproj"]}""" + "\n"));
        var records = await ReadAllAsync(stream);
        Assert.Equal([DotnetWatchStatusTypes.BuildComplete, DotnetWatchStatusTypes.ProcessStarted], records.Select(record => record.Type));
        Assert.Equal("first\nsecond", records[0].Error);
    }

    private static async Task<List<DotnetWatchStatus>> ReadAllAsync(Stream stream)
    {
        var records = new List<DotnetWatchStatus>();
        await foreach (var status in DotnetWatchStatusReader.ReadAsync(stream, CancellationToken.None))
        {
            records.Add(status);
        }
        return records;
    }
}
