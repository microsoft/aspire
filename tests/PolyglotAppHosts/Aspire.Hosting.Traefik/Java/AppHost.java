import aspire.*;

void main() throws Exception {
        var builder = DistributedApplication.CreateBuilder();
        var domain = builder.addParameter("domain");
        var acmeEmail = builder.addParameter("acme-email");
        var htpasswd = builder.addParameter("admin-htpasswd", new AddParameterOptions().secret(true));
        var clientSecret = builder.addParameter("sso-client-secret", new AddParameterOptions().secret(true));

        var traefik = builder.addTraefik("traefik")
            .withHostPort(8000)
            .withHostHttpsPort(8443)
            .withDashboard()
            .withLetsEncrypt(acmeEmail)
            .withLetsEncrypt("admin@example.com", new WithLetsEncryptOptions()
                .resolverName("le-http")
                .challenge(TraefikAcmeChallenge.HTTP)
                .staging(true))
            .withHttpsRedirect()
            .withTlsDomain(ReferenceExpression.refExpr("%s", domain), new ReferenceExpression[] { ReferenceExpression.refExpr("*.%s", domain) })
            .withEntryPoint("postgres", 5432)
            .withEntryPoint("dns", 53, new WithEntryPointOptions().scheme("udp"))
            .withLogLevel(TraefikLogLevel.DEBUG)
            .withAccessLogs(logs -> {
                logs.setFormat(TraefikAccessLogFormat.JSON);
                logs.statusCodes().add("400-599");
            })
            .withDockerSocketProxy()
            .withDockerSocketPath("/var/run/docker.sock")
            .withExternalEntryPoint("https", true, new WithExternalEntryPointOptions().port(8443));

        var headers = traefik.addSecurityHeaders("security-headers");
        var compress = traefik.addCompress("compress");
        var defaults = traefik.addChain("defaults", new TraefikMiddlewareResource[] { headers, compress });
        var adminAuth = traefik.addBasicAuth("admin-auth", new ParameterResource[] { htpasswd });
        var rateLimit = traefik.addRateLimit("rate-limit", 20, new AddRateLimitOptions().burst(40));
        var grpcWeb = traefik.addGrpcWeb("grpc-web", new String[] { "https://app.example.com" });
        var custom = traefik.addMiddleware("custom", "contentType")
            .withOption("autoDetect", true);

        traefik.withEntryPointMiddleware("websecure", new TraefikMiddlewareResource[] { headers });
        traefik.withDashboardRoute(ReferenceExpression.refExpr("traefik.%s", domain), new TraefikMiddlewareResource[] { adminAuth });

        var issuer = ReferenceExpression.refExpr("https://auth.%s/realms/demo", domain);
        var sso = traefik.addOAuth2ProxyForwardAuth("sso", issuer, "oauth2-proxy", clientSecret);

        var whoami = builder.addContainer("whoami", "traefik/whoami");
        whoami.withHttpEndpoint(new WithHttpEndpointOptions().name("http").targetPort(80));
        whoami.withTraefikRoute(traefik, ReferenceExpression.refExpr("whoami.%s", domain), new TraefikMiddlewareResource[0]);
        whoami.withTraefikRoute(traefik, ReferenceExpression.refExpr("admin.%s", domain), new TraefikMiddlewareResource[] { adminAuth });
        whoami.withTraefikMiddleware(new TraefikMiddlewareResource[] { defaults });
        whoami.withTraefikRouteOptions(traefik, route -> {
            var sticky = new TraefikStickySessionOptions();
            sticky.setCookieName("affinity");
            sticky.setSecure(true);
            sticky.setSameSite(TraefikSameSiteMode.LAX);
            sticky.setMaxAgeSeconds(3600);
            var healthCheck = new TraefikHealthCheckOptions();
            healthCheck.setPath("/health");
            healthCheck.setIntervalSeconds(10);
            healthCheck.setTimeoutSeconds(2);
            route.setHost(ReferenceExpression.refExpr("app.%s", domain))
                .setPathPrefix("/api")
                .setStripPathPrefix(true)
                .setRouterName("whoami-api")
                .setPriority(10)
                .setPassHostHeader(false)
                .setStickySession(sticky)
                .setHealthCheck(healthCheck)
                .setUseResourceProbe(false);
            route.methods().add("GET");
        });

        var secure = builder.addContainer("secure", "traefik/whoami");
        secure.withHttpEndpoint(new WithHttpEndpointOptions().name("http").targetPort(80));
        secure.withTraefikRoute(traefik, ReferenceExpression.refExpr("secure.%s", domain), new TraefikMiddlewareResource[] { sso, rateLimit, custom });

        var grpc = builder.addContainer("grpc", "myregistry/grpc");
        grpc.withHttpEndpoint(new WithHttpEndpointOptions().name("http").targetPort(8080));
        grpc.withTraefikRoute(traefik, ReferenceExpression.refExpr("grpc.%s", domain), new TraefikMiddlewareResource[] { grpcWeb });

        var db = builder.addContainer("db", "postgres");
        db.withEndpoint(new WithEndpointOptions().name("tcp").targetPort(5432));
        db.withTraefikTcpRouteOnEntryPoint(traefik, "postgres", "tcp");
        db.withTraefikTcpRouteOptions(traefik, route -> route
            .setEntryPoint("websecure")
            .setHostSni(ReferenceExpression.refExpr("db.%s", domain))
            .setTls(true)
            .setCertResolver("le-http")
            .setRouterName("db-tls"));

        var dns = builder.addContainer("dns", "coredns/coredns");
        dns.withEndpoint(new WithEndpointOptions().name("udp").targetPort(53).protocol(ProtocolType.UDP));
        dns.withTraefikUdpRouteOnEntryPoint(traefik, "dns");

        var _publicUrl = whoami.getTraefikUrl();
        builder.build().run();
    }
