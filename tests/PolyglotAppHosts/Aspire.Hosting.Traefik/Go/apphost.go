package main

import (
	"log"

	"apphost/modules/aspire"
)

func main() {
	builder, err := aspire.CreateBuilder()
	if err != nil {
		log.Fatal(aspire.FormatError(err))
	}

	domain := builder.AddParameter("domain")
	acmeEmail := builder.AddParameter("acme-email")
	htpasswd := builder.AddParameter("admin-htpasswd", &aspire.AddParameterOptions{Secret: aspire.BoolPtr(true)})
	clientSecret := builder.AddParameter("sso-client-secret", &aspire.AddParameterOptions{Secret: aspire.BoolPtr(true)})

	httpChallenge := aspire.TraefikAcmeChallengeHttp
	traefik := builder.AddTraefik("traefik").
		WithHostPort(aspire.Float64Ptr(8000)).
		WithHostHttpsPort(aspire.Float64Ptr(8443)).
		WithDashboard().
		WithLetsEncrypt(acmeEmail).
		WithLetsEncrypt("admin@example.com", &aspire.WithLetsEncryptOptions{
			ResolverName: aspire.StringPtr("le-http"),
			Challenge:    &httpChallenge,
			Staging:      aspire.BoolPtr(true),
		}).
		WithHttpsRedirect().
		WithTlsDomain(aspire.RefExpr("%v", domain), &aspire.WithTlsDomainOptions{
			Sans: []*aspire.ReferenceExpression{aspire.RefExpr("*.%v", domain)},
		}).
		WithEntryPoint("postgres", 5432).
		WithEntryPoint("dns", 53, &aspire.WithEntryPointOptions{Scheme: aspire.StringPtr("udp")}).
		WithLogLevel(aspire.TraefikLogLevelDebug).
		WithAccessLogs(&aspire.WithAccessLogsOptions{
			Configure: func(logs aspire.TraefikAccessLogOptions) {
				logs.SetFormat(aspire.TraefikAccessLogFormatJson)
				_ = logs.StatusCodes().Add("400-599")
			},
		}).
		WithDockerSocketProxy().
		WithDockerSocketPath("/var/run/docker.sock").
		WithExternalEntryPoint("https", true, &aspire.WithExternalEntryPointOptions{Port: aspire.Float64Ptr(8443)})

	headers := traefik.AddSecurityHeaders("security-headers")
	compress := traefik.AddCompress("compress")
	defaults := traefik.AddChain("defaults", []aspire.TraefikMiddlewareResource{headers, compress})
	adminAuth := traefik.AddBasicAuth("admin-auth", []aspire.ParameterResource{htpasswd})
	rateLimit := traefik.AddRateLimit("rate-limit", 20, &aspire.AddRateLimitOptions{Burst: aspire.Float64Ptr(40)})
	grpcWeb := traefik.AddGrpcWeb("grpc-web", []string{"https://app.example.com"})
	custom := traefik.AddMiddleware("custom", "contentType").
		WithOption("autoDetect", true)

	traefik.WithEntryPointMiddleware("websecure", []aspire.TraefikMiddlewareResource{headers})
	traefik.WithDashboardRoute(aspire.RefExpr("traefik.%v", domain), []aspire.TraefikMiddlewareResource{adminAuth})

	issuer := aspire.RefExpr("https://auth.%v/realms/demo", domain)
	sso := traefik.AddOAuth2ProxyForwardAuth("sso", issuer, "oauth2-proxy", clientSecret)

	sameSite := aspire.TraefikSameSiteModeLax
	whoami := builder.AddContainer("whoami", "traefik/whoami").
		WithHttpEndpoint(&aspire.WithHttpEndpointOptions{Name: aspire.StringPtr("http"), TargetPort: aspire.Float64Ptr(80)}).
		WithTraefikRoute(traefik, aspire.RefExpr("whoami.%v", domain)).
		WithTraefikRoute(traefik, aspire.RefExpr("admin.%v", domain), &aspire.WithTraefikRouteOptions{
			Middlewares: []aspire.TraefikMiddlewareResource{adminAuth},
		}).
		WithTraefikMiddleware([]aspire.TraefikMiddlewareResource{defaults}).
		WithTraefikRouteOptions(traefik, func(route aspire.TraefikRouteOptions) {
			route.SetHost(aspire.RefExpr("app.%v", domain)).
				SetPathPrefix(aspire.StringPtr("/api")).
				SetStripPathPrefix(true).
				SetRouterName(aspire.StringPtr("whoami-api")).
				SetPriority(aspire.Float64Ptr(10)).
				SetPassHostHeader(aspire.BoolPtr(false)).
				SetStickySession(&aspire.TraefikStickySessionOptions{
					CookieName:    aspire.StringPtr("affinity"),
					Secure:        true,
					SameSite:      &sameSite,
					MaxAgeSeconds: aspire.Float64Ptr(3600),
				}).
				SetHealthCheck(&aspire.TraefikHealthCheckOptions{
					Path:            "/health",
					IntervalSeconds: aspire.Float64Ptr(10),
					TimeoutSeconds:  aspire.Float64Ptr(2),
				}).
				SetUseResourceProbe(false)
			_ = route.Methods().Add("GET")
		})

	builder.AddContainer("secure", "traefik/whoami").
		WithHttpEndpoint(&aspire.WithHttpEndpointOptions{Name: aspire.StringPtr("http"), TargetPort: aspire.Float64Ptr(80)}).
		WithTraefikRoute(traefik, aspire.RefExpr("secure.%v", domain), &aspire.WithTraefikRouteOptions{
			Middlewares: []aspire.TraefikMiddlewareResource{sso, rateLimit, custom},
		})

	builder.AddContainer("grpc", "myregistry/grpc").
		WithHttpEndpoint(&aspire.WithHttpEndpointOptions{Name: aspire.StringPtr("http"), TargetPort: aspire.Float64Ptr(8080)}).
		WithTraefikRoute(traefik, aspire.RefExpr("grpc.%v", domain), &aspire.WithTraefikRouteOptions{
			Middlewares: []aspire.TraefikMiddlewareResource{grpcWeb},
		})

	builder.AddContainer("db", "postgres").
		WithEndpoint(&aspire.WithEndpointOptions{Name: aspire.StringPtr("tcp"), TargetPort: aspire.Float64Ptr(5432)}).
		WithTraefikTcpRouteOnEntryPoint(traefik, "postgres", &aspire.WithTraefikTcpRouteOnEntryPointOptions{EndpointName: aspire.StringPtr("tcp")}).
		WithTraefikTcpRouteOptions(traefik, func(route aspire.TraefikTcpRouteOptions) {
			route.SetEntryPoint(aspire.StringPtr("websecure")).
				SetHostSni(aspire.RefExpr("db.%v", domain)).
				SetTls(true).
				SetCertResolver(aspire.StringPtr("le-http")).
				SetRouterName(aspire.StringPtr("db-tls"))
		})

	udp := aspire.ProtocolTypeUdp
	builder.AddContainer("dns", "coredns/coredns").
		WithEndpoint(&aspire.WithEndpointOptions{Name: aspire.StringPtr("udp"), TargetPort: aspire.Float64Ptr(53), Protocol: &udp}).
		WithTraefikUdpRouteOnEntryPoint(traefik, "dns")

	_ = whoami.GetTraefikUrl()
	if err = traefik.Err(); err != nil {
		log.Fatal(aspire.FormatError(err))
	}

	app, err := builder.Build()
	if err != nil {
		log.Fatal(aspire.FormatError(err))
	}
	if err := app.Run(); err != nil {
		log.Fatal(aspire.FormatError(err))
	}
}
