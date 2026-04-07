import aspire.*;

void main() throws Exception {
        var builder = DistributedApplication.CreateBuilder();
        // addRedis - full overload with port and password parameter
        var password = builder.addParameter("redis-password", true);
        var cache = builder.addRedis("cache", new AddRedisOptions().password(password));
        // addRedisWithPort - overload with explicit port
        var cache2 = builder.addRedisWithPort("cache2", 6380.0);
        // withDataVolume + withPersistence - fluent chaining on RedisResource
        cache.withDataVolume(new WithDataVolumeOptions().name("redis-data"));
        cache.withPersistence(new WithPersistenceOptions().interval(600000000.0).keysChangedThreshold(5.0));
        // withDataBindMount on RedisResource
        cache2.withDataBindMount("/tmp/redis-data");
        // withHostPort on RedisResource
        cache.withHostPort(6379.0);
        // withPassword on RedisResource
        var newPassword = builder.addParameter("new-redis-password", true);
        cache2.withPassword(newPassword);
        // withRedisCommander - with configureContainer callback exercising withHostPort
        cache.withRedisCommander(new WithRedisCommanderOptions().configureContainer((commander) -> {
                commander.withHostPort(8081.0);
            }).containerName("my-commander"));
        // withRedisInsight - with configureContainer callback exercising withHostPort, withDataVolume, withDataBindMount
        cache.withRedisInsight(new WithRedisInsightOptions().configureContainer((insight) -> {
                insight.withHostPort(5540.0);
                insight.withDataVolume("insight-data");
                insight.withDataBindMount("/tmp/insight-data");
            }).containerName("my-insight"));
        // ---- Property access on RedisResource (ExposeProperties = true) ----
        var redis = cache;
        var _endpoint = redis.primaryEndpoint();
        var _host = redis.host();
        var _port = redis.port();
        var _tlsEnabled = redis.tlsEnabled();
        var _uri = redis.uriExpression();
        var _cstr = redis.connectionStringExpression();
        builder.build().run();
    }
