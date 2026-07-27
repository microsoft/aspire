//! Configuration loading.
//!
//! Aspire Deck is a drop-in replacement for the Blazor dashboard's inter-process
//! communication, so it reads the *same* environment variables the dashboard
//! reads. We support both the friendly `ASPIRE_*` aliases and the canonical
//! `DASHBOARD__*` configuration keys (which is how .NET surfaces the
//! `Dashboard:...` hierarchical config through environment variables).

use serde::Serialize;

/// Authentication mode for an endpoint. Mirrors the dashboard's auth modes.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum AuthMode {
    Unsecured,
    ApiKey,
    /// Certificate-based auth. Recognized but not fully implemented in the MVP;
    /// treated like `Unsecured` for transport purposes.
    Certificate,
}

impl AuthMode {
    fn parse(value: Option<String>) -> Self {
        match value.as_deref().map(str::trim).map(str::to_ascii_lowercase) {
            Some(ref s) if s == "apikey" => AuthMode::ApiKey,
            Some(ref s) if s == "certificate" || s == "clientcertificate" => AuthMode::Certificate,
            _ => AuthMode::Unsecured,
        }
    }
}

/// Configuration for connecting to the AppHost resource service (the gRPC server
/// that streams resource state and console logs to the dashboard).
#[derive(Debug, Clone)]
pub struct ResourceServiceConfig {
    pub url: Option<String>,
    pub auth_mode: AuthMode,
    pub api_key: Option<String>,
}

/// Configuration for the OTLP ingestion servers that the Deck hosts.
#[derive(Debug, Clone)]
pub struct OtlpConfig {
    pub grpc_url: String,
    pub http_url: String,
    pub auth_mode: AuthMode,
    pub primary_api_key: Option<String>,
    pub secondary_api_key: Option<String>,
}

#[derive(Debug, Clone)]
pub struct DeckConfig {
    pub resource_service: ResourceServiceConfig,
    pub otlp: OtlpConfig,
}

/// A serializable view of the configuration sent to the UI. Secrets are never
/// included.
#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DeckConfigView {
    pub application_name: Option<String>,
    pub resource_service_url: Option<String>,
    pub otlp_grpc_url: Option<String>,
    pub otlp_http_url: Option<String>,
    pub version: String,
}

fn first_env(names: &[&str]) -> Option<String> {
    for name in names {
        if let Ok(value) = std::env::var(name) {
            if !value.is_empty() {
                return Some(value);
            }
        }
    }
    None
}

impl DeckConfig {
    pub fn from_env() -> Self {
        // Resource service endpoint. `ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL` is the
        // friendly alias; `DOTNET_RESOURCE_SERVICE_ENDPOINT_URL` is the legacy
        // alias; `DASHBOARD__RESOURCESERVICECLIENT__URL` is the canonical key.
        let resource_url = first_env(&[
            "ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL",
            "DOTNET_RESOURCE_SERVICE_ENDPOINT_URL",
            "DASHBOARD__RESOURCESERVICECLIENT__URL",
        ]);
        let resource_auth = AuthMode::parse(first_env(&[
            "DASHBOARD__RESOURCESERVICECLIENT__AUTHMODE",
        ]));
        let resource_api_key = first_env(&["DASHBOARD__RESOURCESERVICECLIENT__APIKEY"]);

        // OTLP gRPC endpoint. Default to the conventional 4317/4318 ports used by
        // `aspire dashboard run` when nothing is configured.
        let otlp_grpc_url = first_env(&[
            "ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL",
            "DOTNET_DASHBOARD_OTLP_ENDPOINT_URL",
            "DASHBOARD__OTLP__GRPCENDPOINTURL",
        ])
        .unwrap_or_else(|| "http://localhost:4317".to_string());

        let otlp_http_url = first_env(&[
            "ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL",
            "DOTNET_DASHBOARD_OTLP_HTTP_ENDPOINT_URL",
            "DASHBOARD__OTLP__HTTPENDPOINTURL",
        ])
        .unwrap_or_else(|| "http://localhost:4318".to_string());

        let otlp_auth = AuthMode::parse(first_env(&["DASHBOARD__OTLP__AUTHMODE"]));
        let otlp_primary = first_env(&["DASHBOARD__OTLP__PRIMARYAPIKEY"]);
        let otlp_secondary = first_env(&["DASHBOARD__OTLP__SECONDARYAPIKEY"]);

        DeckConfig {
            resource_service: ResourceServiceConfig {
                url: resource_url,
                auth_mode: resource_auth,
                api_key: resource_api_key,
            },
            otlp: OtlpConfig {
                grpc_url: otlp_grpc_url,
                http_url: otlp_http_url,
                auth_mode: otlp_auth,
                primary_api_key: otlp_primary,
                secondary_api_key: otlp_secondary,
            },
        }
    }

    pub fn view(&self, application_name: Option<String>) -> DeckConfigView {
        DeckConfigView {
            application_name,
            resource_service_url: self.resource_service.url.clone(),
            otlp_grpc_url: Some(self.otlp.grpc_url.clone()),
            otlp_http_url: Some(self.otlp.http_url.clone()),
            version: env!("CARGO_PKG_VERSION").to_string(),
        }
    }
}
