# Aspire Dashboard

The Aspire Dashboard is a browser-based app to view run-time information about your distributed application.

The dashboard shows:

- Resources that make up your app, such as .NET projects, executables and containers.
- Live console logs of resources.
- Live telemetry, such as structured logs, traces and metrics.

## Configuration

The dashboard must be configured when it is started. There are a number of ways to provide configuration:

- Command line arguments.
- Environment variables. The `:` delimiter should be replaced with double underscore (`__`) in environment variable names.
- Optional JSON configuration file. The `DOTNET_DASHBOARD_CONFIG_FILE_PATH` setting can be used to specify a JSON configuration file.

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

- `ASPNETCORE_URLS` specifies one or more HTTP endpoints through which the dashboard frontend is served. The frontend endpoint is used to view the dashboard in a browser. Defaults to http://localhost:18888.
- `DOTNET_DASHBOARD_OTLP_ENDPOINT_URL` specifies the OTLP/gRPC endpoint. OTLP/gRPC endpoint hosts an OTLP service and receives telemetry using gRPC. Defaults to http://localhost:18889.
- `DOTNET_DASHBOARD_OTLP_HTTP_ENDPOINT_URL` specifies the OTLP/HTTP endpoint. OTLP/HTTP endpoint hosts an OTLP service and receives telemetry using Protobuf over HTTP. Defaults to http://localhost:18890.
- `DOTNET_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS` specifies the dashboard doesn't use authentication and accepts anonymous access. This setting is a shortcut to configuring `Dashboard:Frontend:AuthMode` and `Dashboard:Otlp:AuthMode` to `Unsecured`.
- `DOTNET_DASHBOARD_CONFIG_FILE_PATH` specifies the path for an optional JSON configuration file.

### Frontend authentication

The dashboard frontend endpoint can be secured with OpenID Connect (OIDC) or browser token authentication.

It may also be run unsecured. Set `Dashboard:Frontend:AuthMode` to `Unsecured`. The frontend endpoint will allow anonymous access. This setting should only be used during local development. It's not recommended when hosting the dashboard publicly or in other settings.

#### Frontend browser token authentication

Set `Dashboard:Frontend:AuthMode` to `BrowserToken`. Browser token authentication works by the frontend asking for a token. The token can either be entered in the UI or provided as a query string value to the login page. For example, `https://localhost:1234/login?t=TheToken`. When the token is successfully authenticated an auth cookie is persisted to the browser and the browser is redirected to the app.

- `Dashboard:Frontend:BrowserToken` specifies the browser token. If the browser token isn't specified then the dashboard will generate one. Tooling that wants to automate logging in with browser token authentication can specify a token and open a browser with the token in the query string. A new token should be generated each time the dashboard is launched. (optional, string)

#### Frontend OIDC authentication

Set `Dashboard:Frontend:AuthMode` to `OpenIdConnect`, then add the following configuration:

- `Authentication:Schemes:OpenIdConnect:Authority` URL to the identity provider (IdP)
- `Authentication:Schemes:OpenIdConnect:ClientId` Identity of the relying party (RP)
- `Authentication:Schemes:OpenIdConnect:ClientSecret` A secret that only the real RP would know
- Other properties of [`OpenIdConnectOptions`](https://learn.microsoft.com/dotnet/api/microsoft.aspnetcore.builder.openidconnectoptions) specified in configuration container `Authentication:Schemes:OpenIdConnect:*`, such as `Scope`.
- `Dashboard:Frontend:OpenIdConnect:NameClaimType` specifies the claim type(s) that should be used to display the authenticated user's full name. Can be a single claim type or a comma-delimited list of claim types. Defaults to `name`.
- `Dashboard:Frontend:OpenIdConnect:UsernameClaimType` specifies the claim type(s) that should be used to display the authenticated user's username. Can be a single claim type or a comma-delimited list of claim types. Defaults to `preferred_username`.
- `Dashboard:Frontend:OpenIdConnect:RequiredClaimType` specifies the (optional) claim that be present for authorized users. Defaults to empty.
- `Dashboard:Frontend:OpenIdConnect:RequiredClaimValue` specifies the (optional) value of the required claim. Only used if `Dashboard:Frontend:OpenIdConnect:RequireClaimType` is also specified. Defaults to empty.

#### Memory limits

- `Dashboard:Frontend:MaxConsoleLogCount` specifies the (optional) maximum number of console log messages to keep in memory. Defaults to 10,000. When the limit is reached, the oldest messages are removed.

### OTLP authentication

The OTLP endpoint can be secured with [client certificate](https://learn.microsoft.com/aspnet/core/security/authentication/certauth) or API key authentication.

It may also be run unsecured. Set `Dashboard:Otlp:AuthMode` to `Unsecured`. The OTLP endpoint will allow anonymous access. This setting is used during local development, but is not recommended if you attempt to host the dashboard in other settings.

For more information, see the official Aspire docs—[Dashboard configuration: OTLP authentication](https://aspire.dev/dashboard/configuration#otlp-authentication).

#### OTLP client certification authentication

For client certification authentication, set `Dashboard:Otlp:AuthMode` to `Certificate`.

#### OTLP API key authentication

For API key authentication, set `Dashboard:Otlp:AuthMode` to `ApiKey`, then add the following configuration:

- `Dashboard:Otlp:PrimaryApiKey` specifies the primary API key. (required, string)
- `Dashboard:Otlp:SecondaryApiKey` specifies the secondary API key. (optional, string)

For more information, see [Security considerations for running the Aspire dashboard: Secure telemetry endpoint](https://aspire.dev/dashboard/security-considerations#secure-telemetry-endpoint).

### Resources

- `Dashboard:ResourceServiceClient:Url` specifies the gRPC endpoint to which the dashboard connects for its data. There's no default. If this variable is unspecified, the dashboard shows OTEL data but no resource list or console logs.

The resource service client supports certificates. Set `Dashboard:ResourceServiceClient:AuthMode` to `Certificate`, then add the following configuration:

- `Dashboard:ResourceServiceClient:ClientCertificate:Source` (required) one of:
  - `File` to load the cert from a file path, configured with:
    - `Dashboard:ResourceServiceClient:ClientCertificate:FilePath` (required, string)
    - `Dashboard:ResourceServiceClient:ClientCertificate:Password` (optional, string)
  - `KeyStore` to load the cert from a key store, configured with:
    - `Dashboard:ResourceServiceClient:ClientCertificate:Subject` (required, string)
    - `Dashboard:ResourceServiceClient:ClientCertificate:Store` (optional, [`StoreName`](https://learn.microsoft.com/dotnet/api/system.security.cryptography.x509certificates.storename), defaults to `My`)
    - `Dashboard:ResourceServiceClient:ClientCertificate:Location` (optional, [`StoreLocation`](https://learn.microsoft.com/dotnet/api/system.security.cryptography.x509certificates.storelocation), defaults to `CurrentUser`)

The resource service client supports API keys. Set `Dashboard:ResourceServiceClient:AuthMode` to `ApiKey` and set `Dashboard:ResourceServiceClient:ApiKey` to the required key. This is used when the resource service is configured to require API keys. It causes gRPC calls to include the configured API key in a request header, for the server to validate.

To opt-out of authentication, set `Dashboard:ResourceServiceClient:AuthMode` to `Unsecured`. This completely disables all security for the resource service client. This setting is used during local development, but is not recommended if you attempt to host the dashboard in other settings.

#### Telemetry Limits

Telemetry is stored in-memory. To avoid excessive memory usage, the dashboard has limits on the count and size of stored telemetry. When a count limit is reached, new telemetry is added, and the oldest telemetry is removed. When a size limit is reached, data is truncated to the limit.

- `Dashboard:TelemetryLimits:MaxLogCount` specifies the maximum number of log entries. Defaults to 10,000.
- `Dashboard:TelemetryLimits:MaxTraceCount` specifies the maximum number of traces. Defaults to 10,000.
- `Dashboard:TelemetryLimits:MaxMetricsCount` specifies the maximum number of metric data points. Defaults to 50,000.
- `Dashboard:TelemetryLimits:MaxAttributeCount` specifies the maximum number of attributes on telemetry. Defaults to 128.
- `Dashboard:TelemetryLimits:MaxAttributeLength` specifies the maximum length of attributes. Defaults to unlimited.
- `Dashboard:TelemetryLimits:MaxSpanEventCount` specifies the maximum number of events on span attributes. Defaults to unlimited.

Limits are per-resource. For example, a `MaxLogCount` value of 10,000 configures the dashboard to store up to 10,000 log entries per-resource.

### Other

- `Dashboard:ApplicationName` specifies the application name to be displayed in the UI. This applies only when no resource service URL is specified. When a resource service exists, the service specifies the application name.

## Data collection

The software may collect information about you and your use of the software and send it to Microsoft. Microsoft may use this information to provide services and improve our products and services. You may turn off the telemetry as described in the repository. There are also some features in the software that may enable you and Microsoft to collect data from users of your applications. If you use these features, you must comply with applicable law, including providing appropriate notices to users of your applications together with a copy of Microsoft’s privacy statement. Our privacy statement is located at https://go.microsoft.com/fwlink/?LinkId=521839. You can learn more about data collection and use in the help documentation and our privacy statement. Your use of the software operates as your consent to these practices.

### Opting out of data collection

The Aspire dashboard collects usage telemetry. Learn [what's collected and why](https://aka.ms/aspire/microsoft-collected-telemetry).  To opt out of dashboard telemetry, set the environment variable `ASPIRE_DASHBOARD_TELEMETRY_OPTOUT` to `true` or disable telemetry in Visual Studio or Visual Studio Code.
