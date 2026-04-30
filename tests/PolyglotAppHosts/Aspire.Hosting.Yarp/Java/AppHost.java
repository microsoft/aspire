import aspire.*;

void main() throws Exception {
        var builder = DistributedApplication.CreateBuilder();
        var buildVersion = builder.addParameterFromConfiguration("buildVersion", "MyConfig:BuildVersion");
        var buildSecret = builder.addParameterFromConfiguration("buildSecret", "MyConfig:Secret", true);
        var staticFilesSource = builder.addContainer("static-files-source", "nginx");
        var backend = builder.addContainer("backend", "nginx");
        backend.withHttpEndpoint(new WithHttpEndpointOptions().name("http").targetPort(80.0));
        var backendService = builder.addProject("backend-service", "./src/BackendService");
        var externalBackend = builder.addExternalService("external-backend", "https://example.com");
        externalBackend.withHttpHealthCheck();
        var proxy = builder.addYarp("proxy");
        proxy.withHostPort(8080.0);
        proxy.withHostHttpsPort(8443.0);
        proxy.withEndpointProxySupport(true);
        proxy.withDockerfile("./context");
        proxy.withImageSHA256("abc123def456");
        proxy.withContainerNetworkAlias("myalias");
        proxy.publishAsContainer();
        proxy.publishWithStaticFiles(staticFilesSource);
        proxy.withVolume("/data", new WithVolumeOptions().name("proxy-data"));
        proxy.withBuildArg("BUILD_VERSION", buildVersion);
        proxy.withBuildSecret("MY_SECRET", buildSecret);
        proxy.withConfiguration((config) -> {
            var endpoint = backend.getEndpoint("http");
            var endpointCluster = config.addClusterFromEndpoint(endpoint);
            var resourceCluster = config.addClusterFromResource(backendService);
            var externalServiceCluster = config.addClusterFromExternalService(externalBackend);
            var singleDestinationCluster = config.addClusterWithDestination("single-destination", "https://example.net");
            var multiDestinationCluster = config.addClusterWithDestinations("multi-destination", new String[] { "https://example.org", "https://example.edu" });
            var routeFromEndpoint = config.addRoute("/from-endpoint/{**catchall}", endpoint);
            var routeFromResource = config.addRoute("/from-resource/{**catchall}", backendService);
            var routeFromExternalService = config.addRoute("/from-external/{**catchall}", externalBackend);
            var routeFromString = config.addRoute("/from-string/{**catchall}", "https://example.route");
            var catchAllRoute = config.addCatchAllRoute(endpointCluster);
            var catchAllRouteFromEndpoint = config.addCatchAllRoute(endpoint);
            var catchAllRouteFromResource = config.addCatchAllRoute(backendService);
            var catchAllRouteFromExternalService = config.addCatchAllRoute(externalBackend);
            var catchAllRouteFromString = config.addCatchAllRoute("https://example.catchall");

            var forwarderRequestConfig = new YarpForwarderRequestConfig();
            forwarderRequestConfig.setActivityTimeout(30_000_000.0);
            forwarderRequestConfig.setAllowResponseBuffering(true);
            forwarderRequestConfig.setVersion("2.0");
            endpointCluster.withForwarderRequestConfig(forwarderRequestConfig);

            var httpClientConfig = new YarpHttpClientConfig();
            httpClientConfig.setDangerousAcceptAnyServerCertificate(true);
            httpClientConfig.setEnableMultipleHttp2Connections(true);
            httpClientConfig.setMaxConnectionsPerServer(10);
            httpClientConfig.setRequestHeaderEncoding("utf-8");
            httpClientConfig.setResponseHeaderEncoding("utf-8");
            endpointCluster.withHttpClientConfig(httpClientConfig);

            var sessionAffinityCookie = new YarpSessionAffinityCookieConfig();
            sessionAffinityCookie.setDomain("example.com");
            sessionAffinityCookie.setHttpOnly(true);
            sessionAffinityCookie.setIsEssential(true);
            sessionAffinityCookie.setPath("/");

            var sessionAffinity = new YarpSessionAffinityConfig();
            sessionAffinity.setAffinityKeyName(".Aspire.Affinity");
            sessionAffinity.setEnabled(true);
            sessionAffinity.setFailurePolicy("Redistribute");
            sessionAffinity.setPolicy("Cookie");
            sessionAffinity.setCookie(sessionAffinityCookie);
            endpointCluster.withSessionAffinityConfig(sessionAffinity);

            var activeHealthCheck = new YarpActiveHealthCheckConfig();
            activeHealthCheck.setEnabled(true);
            activeHealthCheck.setInterval(50_000_000.0);
            activeHealthCheck.setPath("/health");
            activeHealthCheck.setPolicy("ConsecutiveFailures");
            activeHealthCheck.setQuery("probe=1");
            activeHealthCheck.setTimeout(20_000_000.0);

            var passiveHealthCheck = new YarpPassiveHealthCheckConfig();
            passiveHealthCheck.setEnabled(true);
            passiveHealthCheck.setPolicy("TransportFailureRateHealthPolicy");
            passiveHealthCheck.setReactivationPeriod(100_000_000.0);

            var healthCheck = new YarpHealthCheckConfig();
            healthCheck.setAvailableDestinationsPolicy("HealthyOrPanic");
            healthCheck.setActive(activeHealthCheck);
            healthCheck.setPassive(passiveHealthCheck);
            endpointCluster.withHealthCheckConfig(healthCheck);

            var defaultRoute = config.addRoute("/{**catchall}", endpointCluster);
            defaultRoute
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
                .withTransformRequestHeadersAllowed(new String[] { "X-Test-Header", "X-Route-Value" })
                .withTransformCopyResponseHeaders()
                .withTransformCopyResponseTrailers()
                .withTransformResponseHeader("X-Response-Header", "response-value")
                .withTransformResponseHeaderRemove("X-Remove-Response")
                .withTransformResponseHeadersAllowed(new String[] { "X-Response-Header" })
                .withTransformResponseTrailer("X-Response-Trailer", "trailer-value")
                .withTransformResponseTrailerRemove("X-Remove-Trailer")
                .withTransformResponseTrailersAllowed(new String[] { "X-Response-Trailer" });

            var routeMatch = new YarpRouteMatch();
            routeMatch.setPath("/from-endpoint/{**catchall}");
            routeMatch.setMethods(new String[] { "GET", "POST" });
            routeMatch.setHosts(new String[] { "endpoint.example.com" });
            routeFromEndpoint.withMatch(routeMatch);

            routeFromEndpoint.withTransform(Map.ofEntries(Map.entry("PathPrefix", "/endpoint"), Map.entry("RequestHeadersCopy", "true")));
            routeFromResource.withTransform(Map.ofEntries(Map.entry("PathPrefix", "/resource")));
            routeFromExternalService.withTransform(Map.ofEntries(Map.entry("PathPrefix", "/external")));
            routeFromString.withTransform(Map.ofEntries(Map.entry("PathPrefix", "/string")));
            catchAllRoute.withTransform(Map.ofEntries(Map.entry("PathPrefix", "/cluster")));
            catchAllRouteFromEndpoint.withTransform(Map.ofEntries(Map.entry("PathPrefix", "/catchall-endpoint")));
            catchAllRouteFromResource.withTransform(Map.ofEntries(Map.entry("PathPrefix", "/catchall-resource")));
            catchAllRouteFromExternalService.withTransform(Map.ofEntries(Map.entry("PathPrefix", "/catchall-external")));
            catchAllRouteFromString.withTransform(Map.ofEntries(Map.entry("PathPrefix", "/catchall-string")));
            config.addRoute("/resource/{**catchall}", resourceCluster);
            config.addRoute("/external/{**catchall}", externalServiceCluster);
            config.addRoute("/single/{**catchall}", singleDestinationCluster);
            config.addRoute("/multi/{**catchall}", multiDestinationCluster);
        });
        proxy.publishAsConnectionString();
        builder.build().run();
    }
