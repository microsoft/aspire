import { createBuilder } from './.modules/aspire.js';

const builder = await createBuilder();

const buildVersion = await builder.addParameterFromConfiguration("buildVersion", "MyConfig:BuildVersion");
const buildSecret = await builder.addParameterFromConfiguration("buildSecret", "MyConfig:Secret", { secret: true });
const backend = await builder.addContainer("backend", "nginx")
    .withHttpEndpoint({ name: "http", targetPort: 80 });
const backendService = await builder.addProject("backend-service", "./src/BackendService", { launchProfileOrOptions: "http" });
const externalBackend = await builder.addExternalService("external-backend", "https://example.com");
await externalBackend.withHttpHealthCheck();

const proxy = await builder.addYarp("proxy")
    .withHostPort({ port: 8080 })
    .withHostHttpsPort({ port: 8443 })
    .withEndpointProxySupport(true)
    .withDockerfile("./context")
    .withImageSHA256("abc123def456")
    .withContainerNetworkAlias("myalias")
    .publishAsContainer()
    .withStaticFiles();

await proxy.withVolume("/data", { name: "proxy-data" });
await proxy.withBuildArg("BUILD_VERSION", buildVersion);
await proxy.withBuildSecret("MY_SECRET", buildSecret);

await proxy.withConfiguration(async (config) => {
    const endpoint = await backend.getEndpoint("http");
    const endpointCluster = await config.addClusterFromEndpoint(endpoint)
        .withForwarderRequestConfig({
            activityTimeout: 30_000_000,
            allowResponseBuffering: true,
            version: "2.0",
        })
        .withHttpClientConfig({
            dangerousAcceptAnyServerCertificate: true,
            enableMultipleHttp2Connections: true,
            maxConnectionsPerServer: 10,
            requestHeaderEncoding: "utf-8",
            responseHeaderEncoding: "utf-8",
        })
        .withSessionAffinityConfig({
            affinityKeyName: ".Aspire.Affinity",
            enabled: true,
            failurePolicy: "Redistribute",
            policy: "Cookie",
            cookie: {
                domain: "example.com",
                httpOnly: true,
                isEssential: true,
                path: "/",
            },
        })
        .withHealthCheckConfig({
            availableDestinationsPolicy: "HealthyOrPanic",
            active: {
                enabled: true,
                interval: 50_000_000,
                path: "/health",
                policy: "ConsecutiveFailures",
                query: "probe=1",
                timeout: 20_000_000,
            },
            passive: {
                enabled: true,
                policy: "TransportFailureRateHealthPolicy",
                reactivationPeriod: 100_000_000,
            },
        });
    const resourceCluster = await config.addClusterFromResource(backendService);
    const externalServiceCluster = await config.addClusterFromExternalService(externalBackend);
    const singleDestinationCluster = await config.addClusterWithDestination("single-destination", "https://example.net");
    const multiDestinationCluster = await config.addClusterWithDestinations("multi-destination", [
        "https://example.org",
        "https://example.edu"
    ]);

    await config.addRoute("/{**catchall}", endpointCluster)
        .withTransformXForwarded()
        .withTransformForwarded()
        .withTransformClientCertHeader("X-Client-Cert")
        .withTransformHttpMethodChange("GET", "POST")
        .withTransformPathSet("/backend/{**catchall}")
        .withTransformPathPrefix("/api")
        .withTransformPathRemovePrefix("/legacy")
        .withTransformPathRouteValues("/api/{id}")
        .withTransformQueryValue("source", "apphost")
        .withTransformQueryRouteValue("routeId", "id")
        .withTransformQueryRemoveKey("remove")
        .withTransformCopyRequestHeaders()
        .withTransformUseOriginalHostHeader()
        .withTransformRequestHeader("X-Test-Header", "test-value")
        .withTransformRequestHeaderRouteValue("X-Route-Value", "id")
        .withTransformRequestHeaderRemove("X-Remove-Request")
        .withTransformRequestHeadersAllowed(["X-Test-Header", "X-Route-Value"])
        .withTransformCopyResponseHeaders()
        .withTransformCopyResponseTrailers()
        .withTransformResponseHeader("X-Response-Header", "response-value")
        .withTransformResponseHeaderRemove("X-Remove-Response")
        .withTransformResponseHeadersAllowed(["X-Response-Header"])
        .withTransformResponseTrailer("X-Response-Trailer", "trailer-value")
        .withTransformResponseTrailerRemove("X-Remove-Trailer")
        .withTransformResponseTrailersAllowed(["X-Response-Trailer"]);

    await config.addRoute("/from-endpoint/{**catchall}", endpoint)
        .withMatch({
            path: "/from-endpoint/{**catchall}",
            methods: ["GET", "POST"],
            hosts: ["endpoint.example.com"],
        })
        .withTransform({
            PathPrefix: "/endpoint",
            RequestHeadersCopy: "true",
        });
    await config.addRoute("/from-resource/{**catchall}", backendService)
        .withTransform({
            PathPrefix: "/resource",
        });
    await config.addRoute("/from-external/{**catchall}", externalBackend)
        .withTransform({
            PathPrefix: "/external",
        });
    await config.addRoute("/from-string/{**catchall}", "https://example.route")
        .withTransform({
            PathPrefix: "/string",
        });
    await config.addCatchAllRoute(endpointCluster)
        .withTransform({
            PathPrefix: "/cluster",
        });
    await config.addCatchAllRoute(endpoint)
        .withTransform({
            PathPrefix: "/catchall-endpoint",
        });
    await config.addCatchAllRoute(backendService)
        .withTransform({
            PathPrefix: "/catchall-resource",
        });
    await config.addCatchAllRoute(externalBackend)
        .withTransform({
            PathPrefix: "/catchall-external",
        });
    await config.addCatchAllRoute("https://example.catchall")
        .withTransform({
            PathPrefix: "/catchall-string",
        });

    await config.addRoute("/resource/{**catchall}", resourceCluster);
    await config.addRoute("/external/{**catchall}", externalServiceCluster);
    await config.addRoute("/single/{**catchall}", singleDestinationCluster);
    await config.addRoute("/multi/{**catchall}", multiDestinationCluster);
});

await proxy.publishAsConnectionString();

await builder.build().run();
