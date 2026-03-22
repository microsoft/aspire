import { createBuilder } from './.modules/aspire.js';

const builder = await createBuilder();

const buildVersion = await builder.addParameterFromConfiguration("buildVersion", "MyConfig:BuildVersion");
const buildSecret = await builder.addParameterFromConfiguration("buildSecret", "MyConfig:Secret", { secret: true });
const staticFilesSource = await builder.addContainer("static-files-source", "nginx");
const backend = await builder.addContainer("backend", "nginx")
    .withHttpEndpoint({ name: "http", targetPort: 80 });
const externalBackend = await builder.addExternalService("external-backend", "https://example.com");

const proxy = await builder.addYarp("proxy")
    .withHostPort({ port: 8080 })
    .withHostHttpsPort({ port: 8443 })
    .withEndpointProxySupport(true)
    .withDockerfile("./context")
    .withImageSHA256("abc123def456")
    .withContainerNetworkAlias("myalias")
    .publishAsContainer()
    .publishWithStaticFiles(staticFilesSource);

await proxy.withVolume("/data", { name: "proxy-data" });
await proxy.withBuildArg("BUILD_VERSION", buildVersion);
await proxy.withBuildSecret("MY_SECRET", buildSecret);

await proxy.withConfiguration(async (config) => {
    const endpoint = await backend.getEndpoint("http");
    const endpointCluster = await config.addClusterFromEndpoint(endpoint);
    const resourceCluster = await config.addClusterFromResource(backend);
    const externalServiceCluster = await config.addClusterFromExternalService(externalBackend);
    const singleDestinationCluster = await config.addClusterWithDestination("single-destination", "https://example.net");
    const multiDestinationCluster = await config.addClusterWithDestinations("multi-destination", [
        "https://example.org",
        "https://example.edu"
    ]);
    const routeFromEndpoint = await config.addRouteFromEndpoint("/from-endpoint/{**catchall}", endpoint);
    const routeFromResource = await config.addRouteFromResource("/from-resource/{**catchall}", backend);
    const routeFromExternalService = await config.addRouteFromExternalService("/from-external/{**catchall}", externalBackend);
    const catchAllRoute = await config.addCatchAllRoute(endpointCluster);
    const catchAllRouteFromEndpoint = await config.addCatchAllRouteFromEndpoint(endpoint);
    const catchAllRouteFromResource = await config.addCatchAllRouteFromResource(backend);
    const catchAllRouteFromExternalService = await config.addCatchAllRouteFromExternalService(externalBackend);

    await endpointCluster.withForwarderRequestConfig({
        activityTimeout: 30_000_000,
        allowResponseBuffering: true,
        version: "2.0",
    });
    await endpointCluster.withHttpClientConfig({
        dangerousAcceptAnyServerCertificate: true,
        enableMultipleHttp2Connections: true,
        maxConnectionsPerServer: 10,
        requestHeaderEncoding: "utf-8",
        responseHeaderEncoding: "utf-8",
    });
    await endpointCluster.withSessionAffinityConfig({
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
    });
    await endpointCluster.withHealthCheckConfig({
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

    await routeFromEndpoint.withMatch({
        path: "/from-endpoint/{**catchall}",
        methods: ["GET", "POST"],
        hosts: ["endpoint.example.com"],
    });
    await routeFromEndpoint.withTransform({
        PathPrefix: "/endpoint",
        RequestHeadersCopy: "true",
    });
    await routeFromResource.withTransform({
        PathPrefix: "/resource",
    });
    await routeFromExternalService.withTransform({
        PathPrefix: "/external",
    });
    await catchAllRoute.withTransform({
        PathPrefix: "/cluster",
    });
    await catchAllRouteFromEndpoint.withTransform({
        PathPrefix: "/catchall-endpoint",
    });
    await catchAllRouteFromResource.withTransform({
        PathPrefix: "/catchall-resource",
    });
    await catchAllRouteFromExternalService.withTransform({
        PathPrefix: "/catchall-external",
    });

    await config.addRoute("/resource/{**catchall}", resourceCluster);
    await config.addRoute("/external/{**catchall}", externalServiceCluster);
    await config.addRoute("/single/{**catchall}", singleDestinationCluster);
    await config.addRoute("/multi/{**catchall}", multiDestinationCluster);
});

await proxy.publishAsConnectionString();

await builder.build().run();
