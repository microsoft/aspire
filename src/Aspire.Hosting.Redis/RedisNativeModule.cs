// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting;

/// <summary>
/// Well-known Redis modules that are included in the Redis container image from version 8 onward.
/// </summary>
/// <remarks>
/// See https://redis.io/blog/redis-8-ga/.
/// </remarks>
public enum RedisNativeModule
{
    /// <summary>
    /// Redis JSON module for storing, updating, and querying JSON documents in Redis.
    /// </summary>
    Json,

    /// <summary>
    /// Redis Search module for secondary indexing and querying of data stored in Redis.
    /// </summary>
    Search,

    /// <summary>
    /// Redis Bloom Filter module for probabilistic data structures including Bloom filters, Cuckoo filters, Count-Min Sketches, and Top-K filters.
    /// </summary>
    BloomFilter,

    /// <summary>
    /// Redis TimeSeries module for efficient storage and querying of time series data in Redis.
    /// </summary>
    TimeSeries,
}
