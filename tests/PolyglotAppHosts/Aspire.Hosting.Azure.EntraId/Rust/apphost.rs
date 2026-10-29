#[path = ".aspire/modules/mod.rs"]
mod aspire;

use aspire::*;
use serde_json::json;

fn main() -> Result<(), Box<dyn std::error::Error>> {
    let builder = create_builder(None)?;
    let tenant_id = builder.add_parameter("entra-tenant", None, None, None)?;
    let client_id = builder.add_parameter("entra-client", None, None, None)?;
    let client_secret = builder.add_parameter("entra-secret", None, None, Some(true))?;
    let certificate_password = builder.add_parameter("certificate-password", None, None, Some(true))?;
    let redirect_uris = builder.add_parameter("redirect-uris", None, None, None)?;
    let redirect_host = builder.add_parameter("redirect-host", None, None, None)?;
    let owner_id = builder.add_parameter("entra-owner", None, None, None)?;
    let example_tenant = "aaaabbbb-0000-cccc-1111-dddd2222eeee";
    let example_client = "00001111-aaaa-2222-bbbb-3333cccc4444";

    let container = builder.add_container("api", json!("mcr.microsoft.com/dotnet/samples:aspnetapp"))?;
    container.with_http_endpoint(None, Some(8080.0), Some("http"), None, None)?;
    let executable = builder.add_executable("worker", "dotnet", ".", vec!["--info".into()])?;
    let endpoint = container.get_endpoint("http")?;
    let identity = builder.add_azure_user_assigned_identity("identity")?;

    // Existing registrations: configuration only, with literal or parameter IDs in any combination.
    let existing_entra = builder.add_existing_entra_application("existing-entra", serialize_handle(&tenant_id), json!(example_client))?;
    builder.add_existing_entra_application("existing-entra-literals", json!(example_tenant), json!(example_client))?;
    builder.add_existing_entra_application("existing-entra-parameters", serialize_handle(&tenant_id), serialize_handle(&client_id))?;
    existing_entra
        .with_instance("https://login.microsoftonline.com/")?
        .with_sign_in_audience(EntraIdSignInAudience::AzureADMultipleOrgs)?
        .with_audience("api://example")?
        .with_client_secret(&client_secret)?
        .with_federated_managed_identity_credential(
            example_client,
            Some(EntraIdTokenExchangeOptions {
                token_exchange_url: Some("api://AzureADTokenExchange".into()),
                token_exchange_authority: Some("https://login.microsoftonline.com/".into()),
            }),
        )?
        .with_certificate_from_key_vault("https://vault.vault.azure.net", "certificate")?
        .with_certificate_thumbprint("CurrentUser/My", "thumbprint")?
        .with_certificate_distinguished_name("CurrentUser/My", "CN=certificate")?
        .with_certificate_from_file("/certs/client.pfx", Some(&certificate_password))?
        .with_signed_assertion_file(Some("/tokens/assertion"))?
        .with_signed_assertion_file(None)?
        .with_send_x5_c()?
        .with_client_capability("cp1")?
        .with_allow_web_api_to_be_authorized_by_acl()?
        .with_extra_query_parameter("domain_hint", "contoso.com")?;
    container.with_reference(serialize_handle(&existing_entra), Some("AUTH"), None, None)?;
    executable.with_reference(serialize_handle(&existing_entra), None, None, None)?;

    let fluent_existing_entra = builder.add_entra_id_application("fluent-existing-entra")?;
    fluent_existing_entra.as_existing_application(json!(example_tenant), serialize_handle(&client_id))?;

    // An existing API registration is configuration only: its URI and scope IDs describe what the registration already defines.
    let existing_api = builder.add_existing_entra_application("existing-api", json!(example_tenant), json!(example_client))?;
    existing_api.with_application_id_uri(Some("api://contoso-orders"))?;
    existing_api.with_scope(
        "orders.read",
        "Read orders.",
        Some(EntraIdScopeOptions {
            id: Some("33334444-aaaa-5555-bbbb-6666cccc7777".into()),
            ..Default::default()
        }),
    )?;

    // Registrations created by Aspire.
    let created_entra = builder.add_entra_id_application("created-entra")?;
    created_entra
        .with_owners(json!(example_client))?
        .with_owners(serialize_handle(&owner_id))?
        .with_application_owners(serialize_handle(&owner_id))?
        .with_service_principal_owners(json!(example_client))?
        .with_id_token_issuance()?
        .with_app_role("User", "User", "Can use the API.", None, None, None)?
        .with_app_role(
            "Admin",
            "Administrator",
            "Can administer the API.",
            Some(vec!["User".into(), "Application".into()]),
            Some("11112222-aaaa-3333-bbbb-4444cccc5555"),
            Some(false),
        )?
        .with_redirect_uris(json!("https://localhost/signin"), None)?
        .with_redirect_uris(serialize_handle(&redirect_uris), None)?
        .with_redirect_uris(serialize_handle(&redirect_host), Some(vec!["/signin".into(), "/signout".into()]))?
        .with_redirect_uris(serialize_handle(&endpoint), Some(vec!["/signin".into()]))?;
    container.with_reference(serialize_handle(&created_entra), None, None, None)?;
    created_entra
        .publish_with_federated_managed_identity_credential(None)?
        .publish_with_federated_managed_identity_credential(Some(serialize_handle(&container)))?
        .publish_with_federated_managed_identity_credential(Some(serialize_handle(&identity)))?;

    // An API exposed by one registration and called by another.
    let api_entra = builder.add_entra_id_application("api-entra")?;
    api_entra
        .with_application_id_uri(None)?
        .with_application_id_uri(Some("api://contoso-api"))?
        .with_scope("access_as_user", "Allows the app to call the API.", None)?
        .with_scope(
            "admin",
            "Allows the app to administer the API.",
            Some(EntraIdScopeOptions {
                display_name: Some("Administer".into()),
                user_consent_display_name: Some("Administer on your behalf".into()),
                user_consent_description: Some("Allows the app to administer the API for you.".into()),
                requires_admin_consent: Some(true),
                is_enabled: Some(false),
                id: Some("11112222-aaaa-3333-bbbb-4444cccc5555".into()),
            }),
        )?;
    let scope_uri = api_entra.get_scope_uri("access_as_user")?;
    container.with_environment("API_SCOPE", scope_uri.to_json())?;
    let pre_authorized_entra = builder.add_entra_id_application("pre-authorized-entra")?;
    api_entra.with_pre_authorized_application(&pre_authorized_entra, vec!["access_as_user".into()])?;

    let read_permission = EntraIdExternalApiPermission {
        name: "User.Read".into(),
        ..Default::default()
    };
    let read_all_permission = EntraIdExternalApiPermission {
        name: "User.Read.All".into(),
        r#type: Some(EntraIdPermissionType::Application),
        ..Default::default()
    };
    created_entra
        .with_api_permissions(&api_entra, vec!["access_as_user".into()])?
        .with_admin_consent(&api_entra, vec!["access_as_user".into()])?
        .with_api_permissions(&existing_api, vec!["orders.read".into()])?
        .with_external_api_permissions(
            json!(EntraIdWellKnownApi::MicrosoftGraph),
            vec![read_permission.clone(), read_all_permission.clone()],
        )?
        .with_external_api_permissions(
            json!(EntraIdCustomApi {
                display_name: "Contoso API".into(),
                application_id: "99998888-aaaa-7777-bbbb-6666cccc5555".into(),
                resource_uri: "api://contoso".into(),
                base_url: Some("https://contoso.example".into()),
            }),
            vec![EntraIdExternalApiPermission {
                name: "items.read".into(),
                id: Some("22223333-aaaa-4444-bbbb-5555cccc6666".into()),
                ..Default::default()
            }],
        )?
        .with_external_api_admin_consent(
            json!(EntraIdWellKnownApi::MicrosoftGraph),
            vec![read_permission, read_all_permission],
        )?;
    executable.with_entra_id_reference(&created_entra, Some("AUTH"), Some("Apis"))?;

    builder.build()?.run(None)?;
    Ok(())
}
