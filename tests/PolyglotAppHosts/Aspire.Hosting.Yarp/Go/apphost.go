package main

import (
	"log"

	"apphost/modules/aspire"
)

func main() {
	builder, err := aspire.CreateBuilder(nil)
	if err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	buildVersion := builder.AddParameterFromConfiguration("buildVersion", "MyConfig:BuildVersion")
	buildSecret := builder.AddParameterFromConfiguration("buildSecret", "MyConfig:Secret", &aspire.AddParameterFromConfigurationOptions{Secret: aspire.BoolPtr(true)})

	backend := builder.AddContainer("backend", "nginx")
	backend.WithHttpEndpoint(&aspire.WithHttpEndpointOptions{Name: aspire.StringPtr("http"), TargetPort: aspire.Float64Ptr(80)})
	backendService := builder.AddProject("backend-service", "./src/BackendService", &aspire.AddProjectOptions{LaunchProfileOrOptions: "http"})
	externalBackend := builder.AddExternalService("external-backend", "https://example.com")

	proxy := builder.AddYarp("proxy").
		WithHostPort(8080).
		WithHostHttpsPort(8443).
		WithEndpointProxySupport(true).
		WithDockerfile("./context").
		WithImageSHA256("abc123def456").
		WithContainerNetworkAlias("myalias").
		PublishAsContainer().
		WithStaticFiles()

	proxy.WithVolume("/data", &aspire.WithVolumeOptions{Name: aspire.StringPtr("proxy-data")})
	proxy.WithBuildArg("BUILD_VERSION", buildVersion)
	proxy.WithBuildSecret("MY_SECRET", buildSecret)

	if err = proxy.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	proxy.WithConfiguration(func(config aspire.YarpConfigurationBuilder) {
		endpoint := backend.GetEndpoint("http")

		endpointCluster := config.AddClusterFromEndpoint(endpoint).
			WithForwarderRequestConfig(&aspire.YarpForwarderRequestConfig{
				ActivityTimeout:        30_000_000,
				AllowResponseBuffering: true,
				Version:                "2.0",
			}).
			WithHttpClientConfig(&aspire.YarpHttpClientConfig{
				DangerousAcceptAnyServerCertificate: true,
				EnableMultipleHttp2Connections:      true,
				MaxConnectionsPerServer:             10,
				RequestHeaderEncoding:               "utf-8",
				ResponseHeaderEncoding:              "utf-8",
			}).
			WithSessionAffinityConfig(&aspire.YarpSessionAffinityConfig{
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
			}).
			WithHealthCheckConfig(&aspire.YarpHealthCheckConfig{
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
					Enabled:            true,
					Policy:             "TransportFailureRateHealthPolicy",
					ReactivationPeriod: 100_000_000,
				},
			})

		resourceCluster := config.AddClusterFromResource(backendService)
		externalServiceCluster := config.AddClusterFromExternalService(externalBackend)
		singleDestinationCluster := config.AddClusterWithDestination("single-destination", "https://example.net")
		multiDestinationCluster := config.AddClusterWithDestinations("multi-destination", []any{
			"https://example.org",
			"https://example.edu",
		})

		route := config.AddRoute("/{**catchall}", endpointCluster).
			WithTransformXForwarded().
			WithTransformForwarded().
			WithTransformClientCertHeader("X-Client-Cert").
			WithTransformHttpMethodChange("GET", "POST").
			WithTransformPathSet("/backend/{**catchall}").
			WithTransformPathPrefix("/api").
			WithTransformPathRemovePrefix("/legacy").
			WithTransformPathRouteValues("/api/{id}").
			WithTransformQueryValue("source", "apphost").
			WithTransformQueryRouteValue("routeId", "id").
			WithTransformQueryRemoveKey("remove").
			WithTransformCopyRequestHeaders().
			WithTransformUseOriginalHostHeader().
			WithTransformRequestHeader("X-Test-Header", "test-value").
			WithTransformRequestHeaderRouteValue("X-Route-Value", "id").
			WithTransformRequestHeaderRemove("X-Remove-Request").
			WithTransformRequestHeadersAllowed([]string{"X-Test-Header", "X-Route-Value"}).
			WithTransformCopyResponseHeaders().
			WithTransformCopyResponseTrailers().
			WithTransformResponseHeader("X-Response-Header", "response-value").
			WithTransformResponseHeaderRemove("X-Remove-Response").
			WithTransformResponseHeadersAllowed([]string{"X-Response-Header"}).
			WithTransformResponseTrailer("X-Response-Trailer", "trailer-value").
			WithTransformResponseTrailerRemove("X-Remove-Trailer")
		_ = route.WithTransformResponseTrailersAllowed([]string{"X-Response-Trailer"})

		fromEndpointRoute := config.AddRoute("/from-endpoint/{**catchall}", endpoint).
			WithMatch(&aspire.YarpRouteMatch{
				Path:    "/from-endpoint/{**catchall}",
				Methods: []string{"GET", "POST"},
				Hosts:   []string{"endpoint.example.com"},
			}).
			WithTransform(map[string]string{
				"PathPrefix":         "/endpoint",
				"RequestHeadersCopy": "true",
			})
		if err = fromEndpointRoute.Err(); err != nil {
			log.Fatalf(aspire.FormatError(err))
		}

		fromResourceRoute := config.AddRoute("/from-resource/{**catchall}", backendService).
			WithTransform(map[string]string{
				"PathPrefix": "/resource",
			})
		if err = fromResourceRoute.Err(); err != nil {
			log.Fatalf(aspire.FormatError(err))
		}

		fromExternalRoute := config.AddRoute("/from-external/{**catchall}", externalBackend).
			WithTransform(map[string]string{
				"PathPrefix": "/external",
			})
		if err = fromExternalRoute.Err(); err != nil {
			log.Fatalf(aspire.FormatError(err))
		}

		stringCluster := config.AddClusterWithDestination("string-cluster", "https://example.route")
		fromStringRoute := config.AddRoute("/from-string/{**catchall}", stringCluster).
			WithTransform(map[string]string{
				"PathPrefix": "/string",
			})
		if err = fromStringRoute.Err(); err != nil {
			log.Fatalf(aspire.FormatError(err))
		}

		catchAllRoute := config.AddCatchAllRoute(endpointCluster).
			WithTransform(map[string]string{
				"PathPrefix": "/cluster",
			})
		if err = catchAllRoute.Err(); err != nil {
			log.Fatalf(aspire.FormatError(err))
		}

		catchAllEndpointRoute := config.AddCatchAllRoute(endpoint).
			WithTransform(map[string]string{
				"PathPrefix": "/catchall-endpoint",
			})
		if err = catchAllEndpointRoute.Err(); err != nil {
			log.Fatalf(aspire.FormatError(err))
		}

		catchAllResourceRoute := config.AddCatchAllRoute(backendService).
			WithTransform(map[string]string{
				"PathPrefix": "/catchall-resource",
			})
		if err = catchAllResourceRoute.Err(); err != nil {
			log.Fatalf(aspire.FormatError(err))
		}

		catchAllExternalRoute := config.AddCatchAllRoute(externalBackend).
			WithTransform(map[string]string{
				"PathPrefix": "/catchall-external",
			})
		if err = catchAllExternalRoute.Err(); err != nil {
			log.Fatalf(aspire.FormatError(err))
		}

		catchAllStringCluster := config.AddClusterWithDestination("catchall-string-cluster", "https://example.catchall")
		catchAllStringRoute := config.AddCatchAllRoute(catchAllStringCluster).
			WithTransform(map[string]string{
				"PathPrefix": "/catchall-string",
			})
		if err = catchAllStringRoute.Err(); err != nil {
			log.Fatalf(aspire.FormatError(err))
		}

		_ = config.AddRoute("/resource/{**catchall}", resourceCluster)
		_ = config.AddRoute("/external/{**catchall}", externalServiceCluster)
		_ = config.AddRoute("/single/{**catchall}", singleDestinationCluster)
		_ = config.AddRoute("/multi/{**catchall}", multiDestinationCluster)
	})

	proxy.PublishAsConnectionString()
	if err = proxy.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	app, err := builder.Build()
	if err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
}
