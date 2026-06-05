// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Shared.ConsoleLogs;

namespace Aspire.Hosting;

internal interface IConsoleLogsService
{
    IAsyncEnumerable<IReadOnlyList<LogEntry>> GetAllLogsAsync(string resourceName, CancellationToken cancellationToken);
}
