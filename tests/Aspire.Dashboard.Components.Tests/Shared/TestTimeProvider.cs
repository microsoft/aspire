// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Aspire.Dashboard.Components.Tests.Shared;

public sealed class TestTimeProvider : BrowserTimeProvider
{
    private TimeZoneInfo? _localTimeZone;

    public DateTimeOffset UtcNow { get; set; }
    public FakeTimeProvider? Timers { get; init; }

    public TestTimeProvider() : base(NullLoggerFactory.Instance)
    {
        UtcNow = new DateTimeOffset(2025, 12, 20, 23, 59, 59, TimeSpan.Zero);
    }

    public override DateTimeOffset GetUtcNow()
    {
        return UtcNow;
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        Timers?.CreateTimer(callback, state, dueTime, period) ?? base.CreateTimer(callback, state, dueTime, period);

    public override TimeZoneInfo LocalTimeZone => _localTimeZone ??= TimeZoneInfo.CreateCustomTimeZone(nameof(TestTimeProvider), TimeSpan.FromHours(1), nameof(TestTimeProvider), nameof(TestTimeProvider));
}
