#[path = ".aspire/modules/mod.rs"]
mod aspire;

use aspire::*;
use serde_json::json;

fn main() -> Result<(), Box<dyn std::error::Error>> {
    let builder = create_builder(None)?;
    let tenant = builder.add_parameter("tenant", None, None, None)?;
    let client = builder.add_parameter("client", None, None, None)?;
    let secret = builder.add_parameter("secret", None, None, Some(true))?;
    let owner = builder.add_parameter("owner", None, None, None)?;
    let application_owner = builder.add_parameter("application-owner", None, None, None)?;
    let service_principal_owner =
        builder.add_parameter("service-principal-owner", None, None, None)?;
    let redirect = builder.add_parameter("redirect", None, None, None)?;
    let host = builder.add_parameter("host", None, None, None)?;
    let id = "00000000-0000-0000-0000-000000000001";
    let existing =
        builder.add_existing_entra_application("existing", "contoso.onmicrosoft.com", id)?;
    let parameterized = builder.add_existing_entra_application_from_parameters(
        "parameterized",
        &tenant,
        &client,
    )?;
    existing
        .with_client_secret(&secret)?
        .with_instance("https://login.microsoftonline.us/")?
        .with_sign_in_audience(EntraIdSignInAudience::AzureADMultipleOrgs)?
        .with_federated_managed_identity_credential(id)?
        .with_certificate_from_key_vault("https://example.vault.azure.net", "certificate")?
        .with_certificate_thumbprint("CurrentUser/My", "0123456789ABCDEF")?
        .with_certificate_distinguished_name("CurrentUser/My", "CN=example")?
        .with_client_capability("cp1")?
        .with_allow_web_api_to_be_authorized_by_acl()?
        .with_send_x5_c()?
        .with_extra_query_parameter("domain_hint", "contoso.com")?
        .with_audience("api://example")?;
    existing.with_credential(
        &builder.create_entra_id_file_certificate_credential("/certs/client.pfx", Some(&secret))?,
    )?;
    existing.with_credential(&builder.create_entra_id_signed_assertion_file_credential(None)?)?;
    parameterized.with_credential(
        &builder.create_entra_id_signed_assertion_file_credential(Some("/tokens/assertion"))?,
    )?;
    existing.with_credential(&builder.create_entra_id_federated_identity_credential(
        id,
        Some("api://AzureADTokenExchange"),
        Some("https://login.microsoftonline.com/contoso.onmicrosoft.com"),
    )?)?;

    let web = builder.add_container("web", json!("nginx"))?;
    web.with_http_endpoint(None, Some(80.0), None, None, None)?;
    web.with_reference(serialize_handle(&existing), None, None, None)?;
    web.with_entra_id_reference(&parameterized, Some("Identity"))?;
    let created = builder.add_entra_id_application("created")?;
    created
        .with_redirect_uris(vec!["https://localhost:7123/signin-oidc".into()])?
        .with_redirect_uris_from_parameters(vec![redirect])?
        .with_redirect_uris_for_endpoint(&web.get_endpoint("http")?, vec!["/signin-oidc".into()])?
        .with_redirect_uris_for_host(&host, "/signin-oidc", vec!["/signout-callback-oidc".into()])?
        .with_application_owners(vec![id.into()])?
        .with_application_owners_from_parameters(vec![application_owner])?
        .with_service_principal_owners(vec![id.into()])?
        .with_service_principal_owners_from_parameters(vec![service_principal_owner])?
        .with_owners(vec![id.into()])?
        .with_owners_from_parameters(vec![owner])?
        .with_id_token_issuance()?
        .publish_with_federated_managed_identity_credential(None)?;
    web.with_reference(serialize_handle(&created), None, None, None)?;
    let targeted = builder.add_entra_id_application("targeted")?;
    web.with_reference(serialize_handle(&targeted), None, None, None)?;
    targeted.publish_with_federated_managed_identity_credential(Some(serialize_handle(&web)))?;
    let identity = builder.add_azure_user_assigned_identity("identity")?;
    let shared = builder.add_entra_id_application("shared")?;
    shared.publish_with_federated_managed_identity_credential(Some(serialize_handle(&identity)))?;
    web.with_azure_user_assigned_identity(&identity)?;
    web.with_reference(serialize_handle(&shared), None, None, None)?;
    builder
        .add_entra_id_application("converted")?
        .as_existing_application("contoso.onmicrosoft.com", id)?;
    builder
        .add_entra_id_application("converted-parameters")?
        .as_existing_application_from_parameters(&tenant, &client)?;
    builder.build()?.run(None)?;
    Ok(())
}
