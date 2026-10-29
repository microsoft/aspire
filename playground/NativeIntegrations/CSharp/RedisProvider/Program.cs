// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Hosting.Native.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NativeIntegration.Generated;
using NativeIntegrations.Redis;

if (args.FirstOrDefault() is "--redis-health" or "--redis-save" or "--redis-consume")
{
    try
    {
        await using var redis = new RedisClient();
        if (args[0] is "--redis-health" or "--redis-save")
        {
            await redis.ConnectAsync(Environment.GetEnvironmentVariable("REDIS_HOST")!,
                int.Parse(Environment.GetEnvironmentVariable("REDIS_PORT")!, CultureInfo.InvariantCulture),
                Environment.GetEnvironmentVariable("REDIS_PASSWORD")!);
            return args[0] == "--redis-save" ?
                await redis.CommandAsync("SAVE") == "OK" ? 0 : 1 :
                await redis.CommandAsync("PING") == "PONG" ? 0 : 1;
        }
        // The native composite uses hostAndPort + ",password=" + the unmodified
        // secret. Split the first marker only; the password may itself contain it.
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__cache")!;
        var marker = connection.IndexOf(",password=", StringComparison.Ordinal);
        var address = connection[..marker];
        var separator = address.LastIndexOf(':');
        await redis.ConnectAsync(address[..separator].Trim('[', ']'),
            int.Parse(address[(separator + 1)..], CultureInfo.InvariantCulture), connection[(marker + 10)..]);
        const string value = "native-persistent-value";
        if (args[1] == "write" && await redis.CommandAsync("SET", "native-integration-key", value) != "OK")
        {
            return 1;
        }
        return await redis.CommandAsync("GET", "native-integration-key") == value ? 0 : 1;
    }
    catch (Exception)
    {
        // Protocol errors can contain credentials; expose only failure exit status.
        return 1;
    }
}
using var configuration = new ConfigurationManager();
configuration.AddEnvironmentVariables();
var host = new NativeProviderHost(configuration, TimeProvider.System, NullLogger.Instance);
await host.RunAsync(ProviderContract.Create(), CancellationToken.None);
return 0;
