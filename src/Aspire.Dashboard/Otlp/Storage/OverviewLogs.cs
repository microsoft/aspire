// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Otlp.Model;

namespace Aspire.Dashboard.Otlp.Storage;

public sealed record OverviewLogs(int TotalCount, int WarningCount, int ErrorCount, List<OverviewLog> Recent);

public sealed record OverviewLog(long InternalId, DateTime TimeStamp, LogLevel Severity, string Message, OtlpResource Resource)
{
    public bool IsError => Severity is LogLevel.Error or LogLevel.Critical;
    public bool IsWarning => Severity is LogLevel.Warning;
}
