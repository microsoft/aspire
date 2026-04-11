// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Redis;

internal static class RedisContainerImageTags
{
    /// <remarks>docker.io</remarks>
    public const string Registry = "docker.io";

    /// <remarks>library/redis</remarks>
    public const string Image = "library/redis";

    /// <remarks>8.6</remarks>
    public const string Tag = "8.6";

    /// <remarks>RedisCommanderRegistry</remarks>
    public const string RedisCommanderRegistry = "ghcr.io";

    /// <remarks>joeferner/redis-commander</remarks>
    public const string RedisCommanderImage = "joeferner/redis-commander";

    /// <remarks>latest</remarks>
    public const string RedisCommanderTag = "latest"; // There isn't a better tag than 'latest' which is 3 years old.

    /// <remarks>docker.io</remarks>
    public const string RedisInsightRegistry = "docker.io";

    /// <remarks>redis/redisinsight</remarks>
    public const string RedisInsightImage = "redis/redisinsight";

    /// <remarks>3.0</remarks>
    public const string RedisInsightTag = "3.0";
}
