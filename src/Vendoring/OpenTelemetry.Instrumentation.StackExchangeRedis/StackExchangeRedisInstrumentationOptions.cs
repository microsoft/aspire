// Copyright The OpenTelemetry Authors
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using OpenTelemetry.Trace;
using static OpenTelemetry.Internal.DatabaseSemanticConventionHelper;

namespace OpenTelemetry.Instrumentation.StackExchangeRedis;

/// <summary>
/// Options for StackExchange.Redis instrumentation.
/// </summary>
internal class StackExchangeRedisInstrumentationOptions
{
    /// <summary>
    /// Initializes a new instance of the <see cref="StackExchangeRedisInstrumentationOptions"/> class.
    /// </summary>
    public StackExchangeRedisInstrumentationOptions()
        : this(new ConfigurationBuilder().AddEnvironmentVariables().Build())
    {
    }

    internal StackExchangeRedisInstrumentationOptions(IConfiguration configuration)
    {
        var databaseSemanticConvention = GetSemanticConventionOptIn(configuration);
        this.EmitOldAttributes = databaseSemanticConvention.HasFlag(DatabaseSemanticConvention.Old);
        this.EmitNewAttributes = databaseSemanticConvention.HasFlag(DatabaseSemanticConvention.New);
    }

    /// <summary>
    /// Gets or sets the maximum time that should elapse between flushing the internal buffer of Redis profiling sessions and creating <see cref="Activity"/> objects. Default value: 00:00:10.
    /// </summary>
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Gets or sets a value indicating whether the <see cref="StackExchangeRedisConnectionInstrumentation"/> should use reflection to get more detailed
    /// <see cref="SemanticConventions.AttributeDbStatement"/> and <see cref="SemanticConventions.AttributeDbQueryText"/> tag values. Default value:
    /// <see langword="false"/>.
    /// </summary>
    public bool SetVerboseDatabaseStatements { get; set; }

    /// <summary>
    /// Gets or sets a callback method allowing to filter out particular <see cref="RedisInstrumentationContext"/> instances.
    /// </summary>
    /// <remarks>
    /// The filter callback receives the <see cref="RedisInstrumentationContext"/> for the
    /// processed profiled command and returns a boolean indicating whether it should be filtered out.
    /// <list type="bullet">
    /// <item>If filter returns <see langword="true"/> the event is collected.</item>
    /// <item>If filter returns <see langword="false"/> or throws an exception the event is filtered out (NOT collected).</item>
    /// </list>
    /// </remarks>
    public Func<RedisInstrumentationContext, bool>? Filter { get; set; }

    /// <summary>
    /// Gets or sets an action to enrich an Activity.
    /// </summary>
    /// <remarks>
    /// <para><see cref="Activity"/>: the activity being enriched.</para>
    /// <para><see cref="RedisInstrumentationContext"/>: the profiled redis command from which additional information can be extracted to enrich the activity.</para>
    /// </remarks>
    public Action<Activity, RedisInstrumentationContext>? Enrich { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the <see cref="StackExchangeRedisConnectionInstrumentation"/> should enrich Activity with <see cref="ActivityEvent"/> entries about the Redis command processing/lifetime. Defaults to <see langword="true"/>.
    /// </summary>
    public bool EnrichActivityWithTimingEvents { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the old database attributes should be emitted.
    /// </summary>
    internal bool EmitOldAttributes { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the new database attributes should be emitted.
    /// </summary>
    internal bool EmitNewAttributes { get; set; }

    /// <summary>
    /// Gets a value indicating whether commands should be drained from profiling sessions prior to parent <see cref="Activity"/> completion.
    /// Filter and Enrich callbacks have historically been passed completed parent <see cref="Activity"/> instances.
    /// If both callbacks are null, commands may be drained prior to the parent <see cref="Activity"/> completion,
    /// lowering memory pressure and improving performance.
    /// If either callback is set, the old behavior is used to ensure the callbacks are passed completed parent <see cref="Activity"/> instances.
    /// </summary>
    internal bool EnableEarlyCommandDrain => this.Filter == null && this.Enrich == null;
}
