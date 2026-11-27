# Aspire Python validation AppHost
# Mirrors the top-level TypeScript playground surface with Python-style members.

from aspire_app import ReferenceExpression, TraefikAccessLogOptions, TraefikRouteOptions, TraefikTcpRouteOptions, create_builder


def configure_access_logs(logs: TraefikAccessLogOptions) -> None:
    logs.format = "Json"
    logs.status_codes.append("400-599")


def configure_api_route(route: TraefikRouteOptions) -> None:
    route.host = ReferenceExpression.format_string("app.example.com")
    route.path_prefix = "/api"
    route.strip_path_prefix = True
    route.router_name = "whoami-api"
    route.priority = 10
    route.methods.append("GET")
    route.pass_host_header = False
    route.sticky_session = {"cookieName": "affinity", "secure": True, "sameSite": "Lax", "maxAgeSeconds": 3600}
    route.health_check = {"path": "/health", "intervalSeconds": 10, "timeoutSeconds": 2}
    route.use_resource_probe = False


def configure_tcp_route(route: TraefikTcpRouteOptions) -> None:
    route.entry_point = "websecure"
    route.host_sni = ReferenceExpression.format_string("db.example.com")
    route.tls = True
    route.cert_resolver = "le-http"
    route.router_name = "db-tls"


with create_builder() as builder:
    domain = builder.add_parameter("domain")
    acme_email = builder.add_parameter("acme-email")
    htpasswd = builder.add_parameter("admin-htpasswd", secret=True)
    client_secret = builder.add_parameter("sso-client-secret", secret=True)

    traefik = builder.add_traefik("traefik")
    traefik.with_host_port(port=8000)
    traefik.with_host_https_port(port=8443)
    traefik.with_dashboard()
    traefik.with_lets_encrypt(acme_email)
    traefik.with_lets_encrypt("admin@example.com", resolver_name="le-http", challenge="Http", staging=True)
    traefik.with_https_redirect()
    traefik.with_tls_domain(ReferenceExpression.format_string("{0}", domain), sans=[ReferenceExpression.format_string("*.{0}", domain)])
    traefik.with_entry_point("postgres", 5432)
    traefik.with_entry_point("dns", 53, scheme="udp")
    traefik.with_log_level("Debug")
    traefik.with_access_logs(configure=configure_access_logs)
    traefik.with_docker_socket_proxy()
    traefik.with_docker_socket_path("/var/run/docker.sock")
    traefik.with_external_entry_point("https", True, port=8443)

    headers = traefik.add_security_headers("security-headers")
    compress = traefik.add_compress("compress")
    defaults = traefik.add_chain("defaults", [headers, compress])
    admin_auth = traefik.add_basic_auth("admin-auth", [htpasswd])
    rate_limit = traefik.add_rate_limit("rate-limit", 20, burst=40)
    grpc_web = traefik.add_grpc_web("grpc-web", ["https://app.example.com"])
    custom = traefik.add_middleware("custom", "contentType")
    custom.with_option("autoDetect", True)

    traefik.with_entry_point_middleware("websecure", [headers])
    traefik.with_dashboard_route(ReferenceExpression.format_string("traefik.{0}", domain), [admin_auth])

    issuer = ReferenceExpression.format_string("https://auth.{0}/realms/demo", domain)
    sso = traefik.add_o_auth2_proxy_forward_auth("sso", issuer, "oauth2-proxy", client_secret)

    whoami = builder.add_container("whoami", "traefik/whoami")
    whoami.with_http_endpoint(name="http", target_port=80)
    whoami.with_traefik_route(traefik, ReferenceExpression.format_string("whoami.{0}", domain))
    whoami.with_traefik_route(traefik, ReferenceExpression.format_string("admin.{0}", domain), middlewares=[admin_auth])
    whoami.with_traefik_middleware([defaults])
    whoami.with_traefik_route_options(traefik, configure_api_route)

    secure = builder.add_container("secure", "traefik/whoami")
    secure.with_http_endpoint(name="http", target_port=80)
    secure.with_traefik_route(traefik, ReferenceExpression.format_string("secure.{0}", domain), middlewares=[sso, rate_limit, custom])

    grpc = builder.add_container("grpc", "myregistry/grpc")
    grpc.with_http_endpoint(name="http", target_port=8080)
    grpc.with_traefik_route(traefik, ReferenceExpression.format_string("grpc.{0}", domain), middlewares=[grpc_web])

    db = builder.add_container("db", "postgres")
    db.with_endpoint(name="tcp", target_port=5432)
    db.with_traefik_tcp_route_on_entry_point(traefik, "postgres", endpoint_name="tcp")
    db.with_traefik_tcp_route_options(traefik, configure_tcp_route)

    dns = builder.add_container("dns", "coredns/coredns")
    dns.with_endpoint(name="udp", target_port=53, protocol="Udp")
    dns.with_traefik_udp_route_on_entry_point(traefik, "dns")

    _public_url = whoami.get_traefik_url()
    builder.run()
