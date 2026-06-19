// Copyright The OpenTelemetry Authors
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using StackExchange.Redis.Profiling;

namespace OpenTelemetry.Instrumentation.StackExchangeRedis;

/// <summary>
/// Represents contextual information about a profiled Redis command off of which an <see cref="Activity"/> can be created.
/// </summary>
/// <param name="ParentActivity">
/// The parent activity associated with the profiled command.
/// <see cref="Activity.Current"/> is not guaranteed to be the parent activity since commands are profiled asynchronously.
/// </param>
/// <param name="ProfiledCommand">
/// The profiled Redis command.
/// </param>
internal readonly record struct RedisInstrumentationContext(
    Activity? ParentActivity,
    IProfiledCommand ProfiledCommand);
