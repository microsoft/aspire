import {
    createBuilder,
    ProtocolType,
    refExpr,
    TraefikAccessLogFormat,
    TraefikAcmeChallenge,
    TraefikLogLevel,
    TraefikSameSiteMode,
} from './.aspire/modules/aspire.mjs';

const builder = await createBuilder();

const domain = await builder.addParameter("domain");
const acmeEmail = await builder.addParameter("acme-email");
const htpasswd = await builder.addParameter("admin-htpasswd", { secret: true });
const clientSecret = await builder.addParameter("sso-client-secret", { secret: true });

const traefik = await builder.addTraefik("traefik")
    .withHostPort({ port: 8000 })
    .withHostHttpsPort({ port: 8443 })
    .withDashboard()
    .withLetsEncrypt(acmeEmail)
    .withLetsEncrypt("admin@example.com", { resolverName: "le-http", challenge: TraefikAcmeChallenge.Http, staging: true })
    .withHttpsRedirect()
    .withTlsDomain(refExpr`${domain}`, { sans: [refExpr`*.${domain}`] })
    .withEntryPoint("postgres", 5432)
    .withEntryPoint("dns", 53, { scheme: "udp" })
    .withLogLevel(TraefikLogLevel.Debug)
    .withAccessLogs({
        configure: async (logs) => {
            await logs.format.set(TraefikAccessLogFormat.Json);
            await (await logs.statusCodes()).add("400-599");
        },
    })
    .withDockerSocketProxy()
    .withDockerSocketPath("/var/run/docker.sock")
    .withExternalEntryPoint("https", true, { port: 8443 });

const headers = await traefik.addSecurityHeaders("security-headers");
const compress = await traefik.addCompress("compress");
const defaults = await traefik.addChain("defaults", [headers, compress]);
const adminAuth = await traefik.addBasicAuth("admin-auth", [htpasswd]);
const rateLimit = await traefik.addRateLimit("rate-limit", 20, { burst: 40 });
const grpcWeb = await traefik.addGrpcWeb("grpc-web", ["https://app.example.com"]);
const custom = await traefik.addMiddleware("custom", "contentType")
    .withOption("autoDetect", true);

await traefik.withEntryPointMiddleware("websecure", [headers]);
await traefik.withDashboardRoute(refExpr`traefik.${domain}`, [adminAuth]);

const keycloak = await builder.addContainer("keycloak", "quay.io/keycloak/keycloak")
    .withHttpEndpoint({ name: "http", targetPort: 8080 });
const issuer = refExpr`https://auth.${domain}/realms/demo`;
const sso = await traefik.addOAuth2ProxyForwardAuth("sso", issuer, "oauth2-proxy", clientSecret);

const whoami = await builder.addContainer("whoami", "traefik/whoami")
    .withHttpEndpoint({ name: "http", targetPort: 80 })
    .withTraefikRoute(traefik, refExpr`whoami.${domain}`)
    .withTraefikRoute(traefik, refExpr`admin.${domain}`, { middlewares: [adminAuth] })
    .withTraefikMiddleware([defaults]);

await whoami.withTraefikRouteOptions(traefik, async (route) => {
    await route.host.set(refExpr`app.${domain}`);
    await route.pathPrefix.set("/api");
    await route.stripPathPrefix.set(true);
    await route.routerName.set("whoami-api");
    await route.priority.set(10);
    await (await route.methods()).add("GET");
    await route.passHostHeader.set(false);
    await route.stickySession.set({ cookieName: "affinity", secure: true, sameSite: TraefikSameSiteMode.Lax, maxAgeSeconds: 3600 });
    await route.healthCheck.set({ path: "/health", intervalSeconds: 10, timeoutSeconds: 2 });
    await route.useResourceProbe.set(false);
});

await keycloak.withTraefikRoute(traefik, refExpr`auth.${domain}`);

await builder.addContainer("secure", "traefik/whoami")
    .withHttpEndpoint({ name: "http", targetPort: 80 })
    .withTraefikRoute(traefik, refExpr`secure.${domain}`, { middlewares: [sso, rateLimit, custom] });

await builder.addContainer("grpc", "myregistry/grpc")
    .withHttpEndpoint({ name: "http", targetPort: 8080 })
    .withTraefikRoute(traefik, refExpr`grpc.${domain}`, { middlewares: [grpcWeb] });

await builder.addContainer("db", "postgres")
    .withEndpoint({ name: "tcp", targetPort: 5432 })
    .withTraefikTcpRouteOnEntryPoint(traefik, "postgres", { endpointName: "tcp" })
    .withTraefikTcpRouteOptions(traefik, async (route) => {
        await route.entryPoint.set("websecure");
        await route.hostSni.set(refExpr`db.${domain}`);
        await route.tls.set(true);
        await route.certResolver.set("le-http");
        await route.routerName.set("db-tls");
    });

await builder.addContainer("dns", "coredns/coredns")
    .withEndpoint({ name: "udp", targetPort: 53, protocol: ProtocolType.Udp })
    .withTraefikUdpRouteOnEntryPoint(traefik, "dns");

const _publicUrl = await whoami.getTraefikUrl();

await builder.build().run();
