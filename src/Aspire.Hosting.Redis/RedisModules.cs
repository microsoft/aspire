// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting;

/// <summary>
/// Well-known Redis module paths that are included in the Redis container image from version 8 onward.
/// </summary>
/// <remarks>
/// Redis 8 ships a <c>redis-full.conf</c> configuration file that loads the available Redis modules with <c>loadmodule</c>
/// directives. For more information, see the Redis configuration documentation at
/// <see href="https://redis.io/docs/latest/operate/oss_and_stack/management/config/" /> and the Redis modules documentation at
/// <see href="https://redis.io/docs/latest/develop/reference/modules/" />.
/// </remarks>
public static class RedisModules
{
    /// <summary>
    /// The Redis JSON module path for storing, updating, and querying JSON documents in Redis.
    /// </summary>
    [AspireValue("RedisModules")]
    public const string Json = "/usr/local/lib/redis/modules/rejson.so";

    /// <summary>
    /// The Redis Search module path for secondary indexing and querying of data stored in Redis.
    /// </summary>
    [AspireValue("RedisModules")]
    public const string Search = "/usr/local/lib/redis/modules/redisearch.so";

    /// <summary>
    /// The Redis Bloom Filter module path for probabilistic data structures including Bloom filters, Cuckoo filters, Count-Min Sketches, and Top-K filters.
    /// </summary>
    [AspireValue("RedisModules")]
    public const string BloomFilter = "/usr/local/lib/redis/modules/redisbloom.so";

    /// <summary>
    /// The Redis TimeSeries module path for efficient storage and querying of time series data in Redis.
    /// </summary>
    [AspireValue("RedisModules")]
    public const string TimeSeries = "/usr/local/lib/redis/modules/redistimeseries.so";
}
