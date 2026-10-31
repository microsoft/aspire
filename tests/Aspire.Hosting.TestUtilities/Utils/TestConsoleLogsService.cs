// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Aspire.Shared.ConsoleLogs;

namespace Aspire.Hosting.Tests.Utils;

internal sealed class TestConsoleLogsService : IConsoleLogsService
{
    private readonly Func<string, Channel<IReadOnlyList<LogEntry>>>? _getConsoleLogsChannel;

    internal Func<string, bool, CancellationToken, Task<Stream>>? RawOutput { get; init; }

    public Task<Stream> GetRawOutputAsync(string resourceName, bool standardError, CancellationToken cancellationToken) =>
        RawOutput is { } capture ? capture(resourceName, standardError, cancellationToken) :
            throw new NotSupportedException("No raw output stream configured.");

    public TestConsoleLogsService(Func<string, Channel<IReadOnlyList<LogEntry>>>? getConsoleLogsChannel = null)
    {
        _getConsoleLogsChannel = getConsoleLogsChannel;
    }

    public async IAsyncEnumerable<IReadOnlyList<LogEntry>> GetAllLogsAsync(string resourceName, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (_getConsoleLogsChannel == null)
        {
            throw new InvalidOperationException("No console logs writer.");
        }

        var channel = _getConsoleLogsChannel(resourceName);

        await foreach (var result in channel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return result;
        }
    }
}
