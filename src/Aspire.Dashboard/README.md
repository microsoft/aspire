# Aspire Dashboard

The Aspire Dashboard is a browser-based app to view run-time information about your distributed application.

The dashboard shows:

- Resources that make up your app, such as .NET projects, executables and containers.
- Live console logs of resources.
- Live telemetry, such as structured logs, traces and metrics.

## Security considerations

The dashboard can display sensitive information, including resource configuration, environment variables, console logs, and telemetry. Secure the dashboard and its endpoints whenever they are accessible beyond a trusted local development environment.

- Use HTTPS and require authentication for the browser frontend. The dashboard uses browser token authentication by default.
- Authenticate incoming OTLP telemetry with an API key or client certificate. Standalone mode accepts unauthenticated telemetry by default.
- Don't enable anonymous access on an untrusted network. Anyone who can reach the dashboard could view sensitive data or submit telemetry.
- Restrict OTLP endpoint access with network controls. Untrusted senders can spoof telemetry or consume CPU, memory, and network bandwidth. Telemetry storage limits reduce memory usage but aren't request rate or admission limits.

For deployment scenarios and hardening guidance, see [Aspire dashboard security considerations](https://aspire.dev/dashboard/security-considerations/).

## Configuration

The dashboard is configured when it starts up. Configuration includes frontend and OpenTelemetry Protocol (OTLP) addresses, the resource service endpoint, authentication, telemetry limits, and more.

How you configure the dashboard depends on whether it's started by the Aspire AppHost project or run in [standalone mode](https://aspire.dev/dashboard/standalone/).

### Aspire AppHost

The AppHost automatically configures the dashboard, but you can override values if needed. The recommended way to configure the dashboard from the Aspire AppHost is by adding environment variables to the _launchSettings.json_ file. The `:` delimiter must be replaced with double underscore (`__`) in environment variable names. For example, `Dashboard:TelemetryLimits:MaxLogCount` is `DASHBOARD__TELEMETRYLIMITS__MAXLOGCOUNT` as an environment variable.

### Standalone dashboard

There are many ways to provide configuration:

- Command line arguments.
- Environment variables. The `:` delimiter should be replaced with double underscore (`__`) in environment variable names.
- Optional JSON configuration file. The `ASPIRE_DASHBOARD_CONFIG_FILE_PATH` setting can be used to specify a JSON configuration file.

Example JSON configuration file:

```json
{
  "Dashboard": {
    "TelemetryLimits": {
      "MaxLogCount": 1000,
      "MaxTraceCount": 1000,
      "MaxMetricsCount": 1000
    }
  }
}
```

### Common configuration

| Option | Description |
|--------|-------------|
| `ASPNETCORE_URLS`<br/>Default: `http://localhost:18888` | One or more HTTP endpoints through which the dashboard frontend is served. When the dashboard is launched by the Aspire AppHost this address is secured with HTTPS. Securing the dashboard with HTTPS is recommended. |
| `ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL`<br/>Default: `http://localhost:18889` | The [OTLP/gRPC](https://opentelemetry.io/docs/specs/otlp/#otlpgrpc) endpoint. This endpoint hosts an OTLP service and receives telemetry using gRPC. When the dashboard is launched by the Aspire AppHost this address is secured with HTTPS. |
| `ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL`<br/>Default: `http://localhost:18890` | The [OTLP/HTTP](https://opentelemetry.io/docs/specs/otlp/#otlphttp) endpoint. This endpoint hosts an OTLP service and receives telemetry using Protobuf over HTTP. When the dashboard is launched by the Aspire AppHost the OTLP/HTTP endpoint isn't configured by default. |
| `ASPIRE_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS`<br/>Default: `false` | Configures the dashboard to not use authentication and accept anonymous access. This is a shortcut to configuring `Dashboard:Frontend:AuthMode`, `Dashboard:Otlp:AuthMode`, and `Dashboard:Api:AuthMode` to `Unsecured`. See [Dashboard security considerations](https://aspire.dev/dashboard/security-considerations/#anonymous-access) for the security implications. |
| `ASPIRE_DASHBOARD_CONFIG_FILE_PATH`<br/>Default: `null` | The path for an optional JSON configuration file. If the dashboard is run in a Docker container, this is the path to the configuration file in a mounted volume. |
| `ASPIRE_DASHBOARD_FILE_CONFIG_DIRECTORY`<br/>Default: `null` | The directory where the dashboard looks for key-per-file configuration. This value is optional. |
| `ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL`<br/>Default: `null` | The gRPC endpoint to which the dashboard connects for its data. If this value is unspecified, the dashboard shows telemetry data but no resource list or console logs. This is a shortcut to `Dashboard:ResourceServiceClient:Url`. |

### Frontend

The dashboard frontend endpoint authentication is configured with `Dashboard:Frontend:AuthMode`. The frontend can be secured with OpenID Connect (OIDC) or browser token authentication.

Browser token authentication works by asking for a token. The token can either be entered in the UI or provided as a query string value to the login page. For example, `https://localhost:1234/login?t=TheToken`. When the token is successfully authenticated, an auth cookie is persisted to the browser and the browser is redirected to the app.

| Option | Description |
|--------|-------------|
| `Dashboard:Frontend:AuthMode`<br/>Default: `BrowserToken` | Can be set to `BrowserToken`, `OpenIdConnect`, or `Unsecured`. `Unsecured` should only be used during local development. It's not recommended when hosting the dashboard publicly or in other settings. |
| `Dashboard:Frontend:BrowserToken`<br/>Default: `null` | Specifies the browser token. If it isn't specified, the dashboard generates one. Tooling that automates login can specify a token and open a browser with the token in the query string. A new token should be generated each time the dashboard is launched. |
| `Dashboard:Frontend:MaxConsoleLogCount`<br/>Default: `10,000` | An optional limit on the number of console log messages retained in the viewer. When the limit is reached, the oldest messages are removed. |
| `Dashboard:Frontend:PublicUrl`<br/>Default: `null` | Specifies the public URL used to access the dashboard frontend and construct links to it. If a public URL isn't specified, the frontend endpoint is used instead. This setting is important when the dashboard is accessed through a proxy and its endpoint isn't directly reachable. |
| `Dashboard:Frontend:OpenIdConnect:NameClaimType`<br/>Default: `name` | Specifies one or more claim types used to display the authenticated user's full name. Can be a single claim type or a comma-delimited list. |
| `Dashboard:Frontend:OpenIdConnect:UsernameClaimType`<br/>Default: `preferred_username` | Specifies one or more claim types used to display the authenticated user's username. Can be a single claim type or a comma-delimited list. |
| `Dashboard:Frontend:OpenIdConnect:RequiredClaimType`<br/>Default: `null` | Specifies the claim that must be present for authorized users. Authorization fails without this claim. This value is optional. |
| `Dashboard:Frontend:OpenIdConnect:RequiredClaimValue`<br/>Default: `null` | Specifies the value of the required claim. Only used if `Dashboard:Frontend:OpenIdConnect:RequiredClaimType` is also specified. This value is optional. |
| `Dashboard:Frontend:OpenIdConnect:ClaimActions`<br/>Default: `null` | An optional list of claim actions to configure on the OpenID Connect options. Each entry specifies a `ClaimType` and `JsonKey`, with optional `SubKey`, `IsUnique`, and `ValueType` properties. |
| `Authentication:Schemes:OpenIdConnect:Authority`<br/>Default: `null` | URL to the identity provider (IdP). |
| `Authentication:Schemes:OpenIdConnect:ClientId`<br/>Default: `null` | Identity of the relying party (RP). |
| `Authentication:Schemes:OpenIdConnect:ClientSecret`<br/>Default: `null` | A secret that only the real RP would know. |
| Other properties of [`OpenIdConnectOptions`](https://learn.microsoft.com/dotnet/api/microsoft.aspnetcore.authentication.openidconnect.openidconnectoptions)<br/>Default: `null` | Values inside configuration section `Authentication:Schemes:OpenIdConnect:*` are bound to `OpenIdConnectOptions`, such as `Scope`. |

Additional configuration may be required when using `OpenIdConnect` behind a reverse proxy that terminates SSL. Check whether `ASPNETCORE_FORWARDEDHEADERS_ENABLED` needs to be set to `true`. For more information, see [Configure ASP.NET Core to work with proxy servers and load balancers](https://learn.microsoft.com/aspnet/core/host-and-deploy/proxy-load-balancer).

### OTLP

The OTLP endpoint authentication is configured with `Dashboard:Otlp:AuthMode`. The endpoint can be secured with an API key or [client certificate authentication](https://learn.microsoft.com/aspnet/core/security/authentication/certauth).

API key authentication requires each OTLP request to have a valid `x-otlp-api-key` header value matching either the primary or secondary key. Client certificate authentication validates the TLS connection's client certificate using ASP.NET Core certificate authentication and, optionally, an explicit certificate allowlist.

| Option | Description |
|--------|-------------|
| `Dashboard:Otlp:AuthMode`<br/>Default: `Unsecured` | Can be set to `ApiKey`, `ClientCertificate`, or `Unsecured`. `Unsecured` should only be used during local development. It's not recommended when hosting the dashboard publicly or in other settings. |
| `Dashboard:Otlp:PrimaryApiKey`<br/>Default: `null` | Specifies the primary API key. A value with at least 128 bits of entropy is recommended. This value is required when auth mode is `ApiKey`. |
| `Dashboard:Otlp:SecondaryApiKey`<br/>Default: `null` | Specifies an optional secondary API key. If specified, the incoming `x-otlp-api-key` header can match either the primary or secondary key. |
| `Dashboard:Otlp:SuppressUnsecuredMessage`<br/>Default: `false` | Suppresses the unsecured message displayed when `Dashboard:Otlp:AuthMode` is `Unsecured`. This should only be set when an external front door proxy secures access to the endpoint. |
| `Dashboard:Otlp:AllowedCertificates`<br/>Default: `null` | Specifies a list of allowed client certificates. See [Allowed certificates](#allowed-certificates). |
| Properties of [`CertificateAuthenticationOptions`](https://learn.microsoft.com/dotnet/api/microsoft.aspnetcore.authentication.certificate.certificateauthenticationoptions)<br/>Default: `null` | Values inside configuration section `Dashboard:Otlp:CertificateAuthOptions:*` are bound to `CertificateAuthenticationOptions`, such as `AllowedCertificateTypes`. |

For more information, see [Security considerations for running the Aspire dashboard: Secure telemetry endpoint](https://aspire.dev/dashboard/security-considerations/#secure-telemetry-endpoint).

#### Allowed certificates

When using client certificate authentication, `Dashboard:Otlp:AllowedCertificates` can configure an explicit certificate allowlist. Each entry requires a `Thumbprint` containing the SHA256 thumbprint of the certificate to allow. If no allowed certificates are configured, all certificates that pass [ASP.NET Core certificate validation](https://learn.microsoft.com/aspnet/core/security/authentication/certauth#configure-certificate-validation) can authenticate.

Example JSON configuration:

```json
{
  "Dashboard": {
    "Otlp": {
      "AllowedCertificates": [
        {
          "Thumbprint": "HEX_SHA256_THUMBPRINT"
        }
      ]
    }
  }
}
```

### OTLP CORS

Cross-origin resource sharing (CORS) can be configured to allow browser apps to send telemetry to the dashboard. Use the `Dashboard:Otlp:Cors` section to configure allowed origins and headers.

| Option | Description |
|--------|-------------|
| `Dashboard:Otlp:Cors:AllowedOrigins`<br/>Default: `null` | A comma-delimited list of allowed origins. It can include the `*` wildcard to allow any domain. This value is optional. |
| `Dashboard:Otlp:Cors:AllowedHeaders`<br/>Default: `null` | A comma-delimited list of allowed headers. This value is optional. |

The dashboard only supports the `POST` method for sending telemetry and doesn't allow configuration of the allowed methods (`Access-Control-Allow-Methods`) for CORS. For more information, see [Enable browser telemetry](https://aspire.dev/dashboard/enable-browser-telemetry/).

### API

The API section configures the dashboard's Telemetry HTTP API (`/api/telemetry/*`) endpoints. The API is enabled by default and secured with API key authentication. The API key is generated automatically if one isn't provided.

| Option | Description |
|--------|-------------|
| `Dashboard:Api:Disabled`<br/>Default: `false` | Disables the Telemetry HTTP API endpoints. When `true`, the endpoints aren't registered. Set `ASPIRE_DASHBOARD_API_DISABLED=true` to disable the API with an environment variable. |
| `Dashboard:Api:Enabled`<br/>Default: `true` | **Deprecated.** Use `Dashboard:Api:Disabled` instead. When `false`, disables the Telemetry HTTP API endpoints. |
| `Dashboard:Api:AuthMode`<br/>Default: `ApiKey` | Can be set to `ApiKey` or `Unsecured`. `Unsecured` should only be used during local development. |
| `Dashboard:Api:PrimaryApiKey`<br/>Default: Auto-generated | Specifies the primary API key. A value with at least 128 bits of entropy is recommended. When auth mode is `ApiKey` and no key is provided, a 128-bit key is generated at startup. |
| `Dashboard:Api:SecondaryApiKey`<br/>Default: `null` | Specifies an optional secondary API key. |

### Resources

The dashboard connects to a resource service to load and display resource information. The client supports API key and client certificate authentication.

| Option | Description |
|--------|-------------|
| `Dashboard:ResourceServiceClient:Url`<br/>Default: `null` | The gRPC endpoint to which the dashboard connects for its data. If this value is unspecified, the dashboard shows telemetry data but no resource list or console logs. |
| `Dashboard:ResourceServiceClient:AuthMode`<br/>Default: `null` | Can be set to `ApiKey`, `Certificate`, or `Unsecured`. This value is required if a resource service URL is specified. `Unsecured` should only be used during local development. |
| `Dashboard:ResourceServiceClient:ApiKey`<br/>Default: `null` | The API key sent to the resource service in the `x-resource-service-api-key` header. This value is required when auth mode is `ApiKey`. |
| `Dashboard:ResourceServiceClient:ClientCertificate:Source`<br/>Default: `null` | Can be set to `File` or `KeyStore`. This value is required when auth mode is `Certificate`. |
| `Dashboard:ResourceServiceClient:ClientCertificate:FilePath`<br/>Default: `null` | The certificate file path. This value is required when source is `File`. |
| `Dashboard:ResourceServiceClient:ClientCertificate:Password`<br/>Default: `null` | The optional password for the certificate file. |
| `Dashboard:ResourceServiceClient:ClientCertificate:Subject`<br/>Default: `null` | The certificate subject. This value is required when source is `KeyStore`. |
| `Dashboard:ResourceServiceClient:ClientCertificate:Store`<br/>Default: `My` | The certificate [`StoreName`](https://learn.microsoft.com/dotnet/api/system.security.cryptography.x509certificates.storename). |
| `Dashboard:ResourceServiceClient:ClientCertificate:Location`<br/>Default: `CurrentUser` | The certificate [`StoreLocation`](https://learn.microsoft.com/dotnet/api/system.security.cryptography.x509certificates.storelocation). |

#### Telemetry limits

Telemetry is stored in memory. To avoid excessive memory usage, the dashboard limits stored telemetry. Log, trace, and metric retention limits evict the oldest stored values when full; attribute and span-event limits truncate incoming data, and the resource limit rejects telemetry for new resources after the limit is reached.

Telemetry limits have different scopes depending on the telemetry type:

- `MaxLogCount` and `MaxTraceCount` are shared across resources.
- `MaxMetricsCount` is per resource.

| Option | Description |
|--------|-------------|
| `Dashboard:TelemetryLimits:MaxLogCount`<br/>Default: `10,000` | The maximum number of log entries. The limit is shared across resources. |
| `Dashboard:TelemetryLimits:MaxTraceCount`<br/>Default: `10,000` | The maximum number of traces. The limit is shared across resources. |
| `Dashboard:TelemetryLimits:MaxMetricsCount`<br/>Default: `50,000` | The maximum number of metric data points. The limit is per dimension. |
| `Dashboard:TelemetryLimits:MaxAttributeCount`<br/>Default: `128` | The maximum number of attributes on telemetry. |
| `Dashboard:TelemetryLimits:MaxAttributeLength`<br/>Default: `null` | The maximum length of attributes. |
| `Dashboard:TelemetryLimits:MaxSpanEventCount`<br/>Default: `null` | The maximum number of events on span attributes. |
| `Dashboard:TelemetryLimits:MaxResourceCount`<br/>Default: `10,000` | The maximum number of resources tracked by the dashboard. |

### Other

| Option | Description |
|--------|-------------|
| `Dashboard:ApplicationName`<br/>Default: `Aspire` | The application name displayed in the UI. This applies only when no resource service URL is specified. When a resource service exists, the service specifies the application name. |
| `Dashboard:UI:DisableResourceGraph`<br/>Default: `false` | Disables the resource graph UI. |
| `Dashboard:UI:DisableImport`<br/>Default: `false` | Disables the telemetry import UI. |
| `Dashboard:UI:DisableAgentHelp`<br/>Default: `false` | Disables the **AI Agents** button in the dashboard header. When `false`, the button opens a dialog with instructions for using AI coding agents with the dashboard. |

For the maintained configuration reference, see [Aspire dashboard configuration](https://aspire.dev/dashboard/configuration/).

## Data collection

The software may collect information about you and your use of the software and send it to Microsoft. Microsoft may use this information to provide services and improve our products and services. You may turn off the telemetry as described in the repository. There are also some features in the software that may enable you and Microsoft to collect data from users of your applications. If you use these features, you must comply with applicable law, including providing appropriate notices to users of your applications together with a copy of Microsoft’s privacy statement. Our privacy statement is located at https://go.microsoft.com/fwlink/?LinkId=521839. You can learn more about data collection and use in the help documentation and our privacy statement. Your use of the software operates as your consent to these practices.

### Opting out of data collection

Aspire dashboard usage telemetry is collected only when the dashboard is launched through Visual Studio or Visual Studio Code as part of a running Aspire application. To opt out for all users accessing the dashboard, set the `ASPIRE_DASHBOARD_TELEMETRY_OPTOUT` environment variable to `true`. Alternatively, disable telemetry collection in the host IDE.

For details about the data collected and how it's used, see [Microsoft-collected dashboard telemetry](https://aspire.dev/dashboard/microsoft-collected-dashboard-telemetry/).
