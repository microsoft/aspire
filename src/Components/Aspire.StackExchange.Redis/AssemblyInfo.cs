// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.StackExchange.Redis;
using Aspire;
using StackExchange.Redis;

[assembly: ConfigurationSchema("Aspire:StackExchange:Redis", typeof(StackExchangeRedisSettings))]
// Redis 3.2 marks the availability policies as experimental and legacy settings as errors.
// Excluding them keeps configuration binding limited to supported, effective options.
[assembly: ConfigurationSchema(
    "Aspire:StackExchange:Redis:ConfigurationOptions",
    typeof(ConfigurationOptions),
    exclusionPaths: [
        "CircuitBreaker",
        "HighPrioritySocketThreads",
        "PreserveAsyncOrder",
        "ReconnectRetryPolicy",
        "ResponseTimeout",
        "RetryPolicy",
        "SocketManager",
        "UseSsl",
        "WriteBuffer"
    ])]

[assembly: LoggingCategories("StackExchange.Redis")]
