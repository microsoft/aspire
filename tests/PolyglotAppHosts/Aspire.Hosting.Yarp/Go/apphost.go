package main

import (
	"log"

	"apphost/modules/aspire"
)

func main() {
	builder, err := aspire.CreateBuilder(nil)
	if err != nil {
		log.Fatalf("CreateBuilder: %v", err)
	}

	// ---- Parameters ----
	buildVersion := builder.AddParameterFromConfiguration("buildVersion", "MyConfig:BuildVersion")
	buildSecret := builder.AddParameterFromConfigurationWithOpts("buildSecret", "MyConfig:Secret", &aspire.AddParameterFromConfigurationOptions{Secret: aspire.BoolPtr(true)})

	// ---- Resources ----
	backend := builder.AddContainer("backend", "nginx")
	backend.WithHttpEndpointWithOpts(&aspire.WithHttpEndpointOptions{Name: aspire.StringPtr("http"), TargetPort: aspire.Float64Ptr(80)})
	backendService := builder.AddProject("backend-service", "./src/BackendService", "http")
	externalBackend := builder.AddExternalService("external-backend", "https://example.com")

	// ---- Yarp proxy configuration ----
	proxy := builder.AddYarp("proxy").
		WithHostPort(8080).
		WithHostHttpsPort(8443).
		WithEndpointProxySupport(true).
		WithDockerfile("./context").
		WithImageSHA256("abc123def456").
		WithContainerNetworkAlias("myalias").
		PublishAsContainer().
		WithStaticFiles().
		// withVolume — named volume
		WithVolumeWithOpts("/data", &aspire.WithVolumeOptions{Name: aspire.StringPtr("proxy-data")}).
		// withBuildArg / withBuildSecret
		WithBuildArg("BUILD_VERSION", buildVersion).
		WithBuildSecret("MY_SECRET", buildSecret)

	if err = proxy.Err(); err != nil {
		log.Fatalf("proxy: %v", err)
	}

	// ---- WithConfiguration ----
	proxy.WithConfiguration(func(config *aspire.IYarpConfigurationBuilder) {
		// Get endpoint from backend container
		endpoint, err := backend.GetEndpoint("http")
		if err != nil {
			log.Fatalf("GetEndpoint: %v", err)
		}

		// Add cluster from endpoint with various configs
		endpointCluster, err := config.AddClusterFromEndpoint(endpoint)
		if err != nil {
			log.Fatalf("AddClusterFromEndpoint: %v", err)
		}
		endpointCluster, err = endpointCluster.WithForwarderRequestConfig(&aspire.YarpForwarderRequestConfig{
			ActivityTimeout:        30_000_000,
			AllowResponseBuffering: true,
			Version:                "2.0",
		})
		if err != nil {
			log.Fatalf("WithForwarderRequestConfig: %v", err)
		}
		endpointCluster, err = endpointCluster.WithHttpClientConfig(&aspire.YarpHttpClientConfig{
			DangerousAcceptAnyServerCertificate: true,
			EnableMultipleHttp2Connections:      true,
			MaxConnectionsPerServer:             10,
			RequestHeaderEncoding:               "utf-8",
			ResponseHeaderEncoding:              "utf-8",
		})
		if err != nil {
			log.Fatalf("WithHttpClientConfig: %v", err)
		}
		endpointCluster, err = endpointCluster.WithSessionAffinityConfig(&aspire.YarpSessionAffinityConfig{
			AffinityKeyName: ".Aspire.Affinity",
			Enabled:         true,
			FailurePolicy:   "Redistribute",
			Policy:          "Cookie",
			Cookie: &aspire.YarpSessionAffinityCookieConfig{
				Domain:      "example.com",
				HttpOnly:    true,
				IsEssential: true,
				Path:        "/",
			},
		})
		if err != nil {
			log.Fatalf("WithSessionAffinityConfig: %v", err)
		}
		endpointCluster, err = endpointCluster.WithHealthCheckConfig(&aspire.YarpHealthCheckConfig{
			AvailableDestinationsPolicy: "HealthyOrPanic",
			Active: &aspire.YarpActiveHealthCheckConfig{
				Enabled:  true,
				Interval: 50_000_000,
				Path:     "/health",
				Policy:   "ConsecutiveFailures",
				Query:    "probe=1",
				Timeout:  20_000_000,
			},
			Passive: &aspire.YarpPassiveHealthCheckConfig{
				Enabled:           true,
				Policy:            "TransportFailureRateHealthPolicy",
				ReactivationPeriod: 100_000_000,
			},
		})
		if err != nil {
			log.Fatalf("WithHealthCheckConfig: %v", err)
		}

		// Add cluster from resource (ProjectResource)
		resourceCluster, err := config.AddClusterFromResource(aspire.NewIResourceWithServiceDiscovery(backendService.Handle(), backendService.Client()))
		if err != nil {
			log.Fatalf("AddClusterFromResource: %v", err)
		}

		// Add cluster from external service
		externalServiceCluster, err := config.AddClusterFromExternalService(externalBackend)
		if err != nil {
			log.Fatalf("AddClusterFromExternalService: %v", err)
		}

		// Add clusters with destinations (string URLs)
		singleDestinationCluster, err := config.AddClusterWithDestination("single-destination", "https://example.net")
		if err != nil {
			log.Fatalf("AddClusterWithDestination: %v", err)
		}
		multiDestinationCluster, err := config.AddClusterWithDestinations("multi-destination", []any{
			"https://example.org",
			"https://example.edu",
		})
		if err != nil {
			log.Fatalf("AddClusterWithDestinations: %v", err)
		}

		// Add main route with all transforms
		route, err := config.AddRoute("/{**catchall}", endpointCluster)
		if err != nil {
			log.Fatalf("AddRoute: %v", err)
		}
		route, _ = route.WithTransformXForwarded()
		route, _ = route.WithTransformForwarded()
		route, _ = route.WithTransformClientCertHeader("X-Client-Cert")
		route, _ = route.WithTransformHttpMethodChange("GET", "POST")
		route, _ = route.WithTransformPathSet("/backend/{**catchall}")
		route, _ = route.WithTransformPathPrefix("/api")
		route, _ = route.WithTransformPathRemovePrefix("/legacy")
		route, _ = route.WithTransformPathRouteValues("/api/{id}")
		route, _ = route.WithTransformQueryValue("source", "apphost")
		route, _ = route.WithTransformQueryRouteValue("routeId", "id")
		route, _ = route.WithTransformQueryRemoveKey("remove")
		route, _ = route.WithTransformCopyRequestHeaders()
		route, _ = route.WithTransformUseOriginalHostHeader()
		route, _ = route.WithTransformRequestHeader("X-Test-Header", "test-value")
		route, _ = route.WithTransformRequestHeaderRouteValue("X-Route-Value", "id")
		route, _ = route.WithTransformRequestHeaderRemove("X-Remove-Request")
		route, _ = route.WithTransformRequestHeadersAllowed([]string{"X-Test-Header", "X-Route-Value"})
		route, _ = route.WithTransformCopyResponseHeaders()
		route, _ = route.WithTransformCopyResponseTrailers()
		route, _ = route.WithTransformResponseHeader("X-Response-Header", "response-value")
		route, _ = route.WithTransformResponseHeaderRemove("X-Remove-Response")
		route, _ = route.WithTransformResponseHeadersAllowed([]string{"X-Response-Header"})
		route, _ = route.WithTransformResponseTrailer("X-Response-Trailer", "trailer-value")
		route, _ = route.WithTransformResponseTrailerRemove("X-Remove-Trailer")
		_, _ = route.WithTransformResponseTrailersAllowed([]string{"X-Response-Trailer"})

		// Route from endpoint
		fromEndpointRoute, err := config.AddRouteFromEndpoint("/from-endpoint/{**catchall}", endpoint)
		if err != nil {
			log.Fatalf("AddRouteFromEndpoint: %v", err)
		}
		fromEndpointRoute, _ = fromEndpointRoute.WithMatch(&aspire.YarpRouteMatch{
			Path:    "/from-endpoint/{**catchall}",
			Methods: []string{"GET", "POST"},
			Hosts:   []string{"endpoint.example.com"},
		})
		_, _ = fromEndpointRoute.WithTransform(map[string]string{
			"PathPrefix":          "/endpoint",
			"RequestHeadersCopy": "true",
		})

		// Route from resource
		fromResourceRoute, err := config.AddRouteFromResource("/from-resource/{**catchall}", aspire.NewIResourceWithServiceDiscovery(backendService.Handle(), backendService.Client()))
		if err != nil {
			log.Fatalf("AddRouteFromResource: %v", err)
		}
		_, _ = fromResourceRoute.WithTransform(map[string]string{
			"PathPrefix": "/resource",
		})

		// Route from external service
		fromExternalRoute, err := config.AddRouteFromExternalService("/from-external/{**catchall}", externalBackend)
		if err != nil {
			log.Fatalf("AddRouteFromExternalService: %v", err)
		}
		_, _ = fromExternalRoute.WithTransform(map[string]string{
			"PathPrefix": "/external",
		})

		// Route from string — use a destination cluster
		stringCluster, err := config.AddClusterWithDestination("string-cluster", "https://example.route")
		if err != nil {
			log.Fatalf("AddClusterWithDestination string: %v", err)
		}
		fromStringRoute, err := config.AddRoute("/from-string/{**catchall}", stringCluster)
		if err != nil {
			log.Fatalf("AddRoute from string: %v", err)
		}
		_, _ = fromStringRoute.WithTransform(map[string]string{
			"PathPrefix": "/string",
		})

		// CatchAll route from cluster
		catchAllRoute, err := config.AddCatchAllRoute(endpointCluster)
		if err != nil {
			log.Fatalf("AddCatchAllRoute: %v", err)
		}
		_, _ = catchAllRoute.WithTransform(map[string]string{
			"PathPrefix": "/cluster",
		})

		// CatchAll from endpoint
		catchAllEndpointRoute, err := config.AddCatchAllRouteFromEndpoint(endpoint)
		if err != nil {
			log.Fatalf("AddCatchAllRouteFromEndpoint: %v", err)
		}
		_, _ = catchAllEndpointRoute.WithTransform(map[string]string{
			"PathPrefix": "/catchall-endpoint",
		})

		// CatchAll from resource
		catchAllResourceRoute, err := config.AddCatchAllRouteFromResource(aspire.NewIResourceWithServiceDiscovery(backendService.Handle(), backendService.Client()))
		if err != nil {
			log.Fatalf("AddCatchAllRouteFromResource: %v", err)
		}
		_, _ = catchAllResourceRoute.WithTransform(map[string]string{
			"PathPrefix": "/catchall-resource",
		})

		// CatchAll from external service
		catchAllExternalRoute, err := config.AddCatchAllRouteFromExternalService(externalBackend)
		if err != nil {
			log.Fatalf("AddCatchAllRouteFromExternalService: %v", err)
		}
		_, _ = catchAllExternalRoute.WithTransform(map[string]string{
			"PathPrefix": "/catchall-external",
		})

		// CatchAll from string — use a destination cluster
		catchAllStringCluster, err := config.AddClusterWithDestination("catchall-string-cluster", "https://example.catchall")
		if err != nil {
			log.Fatalf("AddClusterWithDestination catchall string: %v", err)
		}
		catchAllStringRoute, err := config.AddCatchAllRoute(catchAllStringCluster)
		if err != nil {
			log.Fatalf("AddCatchAllRoute string: %v", err)
		}
		_, _ = catchAllStringRoute.WithTransform(map[string]string{
			"PathPrefix": "/catchall-string",
		})

		// Routes using named clusters
		resourceRoute, err := config.AddRoute("/resource/{**catchall}", resourceCluster)
		if err != nil {
			log.Fatalf("AddRoute resource: %v", err)
		}
		_ = resourceRoute

		externalRoute, err := config.AddRoute("/external/{**catchall}", externalServiceCluster)
		if err != nil {
			log.Fatalf("AddRoute external: %v", err)
		}
		_ = externalRoute

		singleRoute, err := config.AddRoute("/single/{**catchall}", singleDestinationCluster)
		if err != nil {
			log.Fatalf("AddRoute single: %v", err)
		}
		_ = singleRoute

		multiRoute, err := config.AddRoute("/multi/{**catchall}", multiDestinationCluster)
		if err != nil {
			log.Fatalf("AddRoute multi: %v", err)
		}
		_ = multiRoute
	})

	proxy.PublishAsConnectionString()
	if err = proxy.Err(); err != nil {
		log.Fatalf("proxy after config: %v", err)
	}

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
