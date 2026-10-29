# Aspire Python validation AppHost
# Mirrors the top-level TypeScript playground surface with Python-style members.

from aspire_app import create_builder

EXAMPLE_TENANT = "aaaabbbb-0000-cccc-1111-dddd2222eeee"
EXAMPLE_CLIENT = "00001111-aaaa-2222-bbbb-3333cccc4444"

with create_builder() as builder:
    tenant_id = builder.add_parameter("entra-tenant")
    client_id = builder.add_parameter("entra-client")
    client_secret = builder.add_parameter("entra-secret", secret=True)
    certificate_password = builder.add_parameter("certificate-password", secret=True)
    redirect_uris = builder.add_parameter("redirect-uris")
    redirect_host = builder.add_parameter("redirect-host")
    owner_id = builder.add_parameter("entra-owner")

    container = builder.add_container("api", "mcr.microsoft.com/dotnet/samples:aspnetapp")
    container.with_http_endpoint(name="http", target_port=8080)
    executable = builder.add_executable("worker", "dotnet", ".", ["--info"])
    endpoint = container.get_endpoint("http")
    identity = builder.add_azure_user_assigned_identity("identity")

    # Existing registrations: configuration only, with literal or parameter IDs in any combination.
    existing_entra = builder.add_existing_entra_app("existing-entra", tenant_id, EXAMPLE_CLIENT)
    builder.add_existing_entra_app("existing-entra-literals", EXAMPLE_TENANT, EXAMPLE_CLIENT)
    builder.add_existing_entra_app("existing-entra-parameters", tenant_id, client_id)
    existing_entra.with_instance("https://login.microsoftonline.com/")
    existing_entra.with_sign_in_audience("AzureADMultipleOrgs")
    existing_entra.with_audience("api://example")
    existing_entra.with_client_secret(client_secret)
    existing_entra.with_federated_managed_identity_credential(
        EXAMPLE_CLIENT,
        options={
            "tokenExchangeUrl": "api://AzureADTokenExchange",
            "tokenExchangeAuthority": "https://login.microsoftonline.com/",
        },
    )
    existing_entra.with_certificate_from_key_vault("https://vault.vault.azure.net", "certificate")
    existing_entra.with_certificate_thumbprint("CurrentUser/My", "thumbprint")
    existing_entra.with_certificate_distinguished_name("CurrentUser/My", "CN=certificate")
    existing_entra.with_certificate_from_file("/certs/client.pfx", password=certificate_password)
    existing_entra.with_signed_assertion_file(file_path="/tokens/assertion")
    existing_entra.with_signed_assertion_file()
    existing_entra.with_send_x5_c()
    existing_entra.with_client_capability("cp1")
    existing_entra.with_allow_web_api_to_be_authorized_by_acl()
    existing_entra.with_extra_query_parameter("domain_hint", "contoso.com")
    container.with_reference(existing_entra, connection_name="AUTH")
    executable.with_reference(existing_entra)

    fluent_existing_entra = builder.add_entra_id_app("fluent-existing-entra")
    fluent_existing_entra.as_existing_app(EXAMPLE_TENANT, client_id)

    # An existing API registration is configuration only: its URI and scope IDs describe what the registration already defines.
    existing_api = builder.add_existing_entra_app("existing-api", EXAMPLE_TENANT, EXAMPLE_CLIENT)
    existing_api.with_app_id_uri(app_id_uri="api://contoso-orders")
    existing_api.with_scope("orders.read", "Read orders.", options={"id": "33334444-aaaa-5555-bbbb-6666cccc7777"})

    # Registrations created by Aspire.
    created_entra = builder.add_entra_id_app("created-entra")
    created_entra.with_owners(EXAMPLE_CLIENT)
    created_entra.with_owners(owner_id)
    created_entra.with_app_owners(owner_id)
    created_entra.with_service_principal_owners(EXAMPLE_CLIENT)
    created_entra.with_id_token_issuance()
    created_entra.with_app_role("User", "User", "Can use the API.")
    created_entra.with_app_role(
        "Admin",
        "Administrator",
        "Can administer the API.",
        allowed_member_types=["User", "Application"],
        id="11112222-aaaa-3333-bbbb-4444cccc5555",
        is_enabled=False,
    )
    created_entra.with_redirect_uris("https://localhost/signin")
    created_entra.with_redirect_uris(redirect_uris)
    created_entra.with_redirect_uris(redirect_host, callback_paths=["/signin", "/signout"])
    created_entra.with_redirect_uris(endpoint, callback_paths=["/signin"])
    container.with_reference(created_entra)
    created_entra.publish_with_federated_managed_identity_credential()
    created_entra.publish_with_federated_managed_identity_credential(identity=container)
    created_entra.publish_with_federated_managed_identity_credential(identity=identity)

    # An API exposed by one registration and called by another.
    api_entra = builder.add_entra_id_app("api-entra")
    api_entra.with_app_id_uri()
    api_entra.with_app_id_uri(app_id_uri="api://contoso-api")
    api_entra.with_scope("access_as_user", "Allows the app to call the API.")
    api_entra.with_scope(
        "admin",
        "Allows the app to administer the API.",
        options={
            "displayName": "Administer",
            "userConsentDisplayName": "Administer on your behalf",
            "userConsentDescription": "Allows the app to administer the API for you.",
            "requiresAdminConsent": True,
            "isEnabled": False,
            "id": "11112222-aaaa-3333-bbbb-4444cccc5555",
        },
    )
    scope_uri = api_entra.get_scope_uri("access_as_user")
    container.with_env("API_SCOPE", scope_uri)
    pre_authorized_entra = builder.add_entra_id_app("pre-authorized-entra")
    api_entra.with_pre_authorized_app(pre_authorized_entra, ["access_as_user"])

    created_entra.with_api_permissions(api_entra, ["access_as_user"])
    created_entra.with_admin_consent(api_entra, ["access_as_user"])
    created_entra.with_api_permissions(existing_api, ["orders.read"])
    created_entra.with_external_api_permissions(
        "MicrosoftGraph",
        [
            {"name": "User.Read"},
            {"name": "User.Read.All", "type": "Application"},
        ],
    )
    created_entra.with_external_api_permissions(
        {
            "displayName": "Contoso API",
            "applicationId": "99998888-aaaa-7777-bbbb-6666cccc5555",
            "resourceUri": "api://contoso",
            "baseUrl": "https://contoso.example",
        },
        [{"name": "items.read", "id": "22223333-aaaa-4444-bbbb-5555cccc6666"}],
    )
    created_entra.with_external_api_admin_consent(
        "MicrosoftGraph",
        [
            {"name": "User.Read"},
            {"name": "User.Read.All", "type": "Application"},
        ],
    )
    executable.with_entra_id_reference(created_entra, connection_name="AUTH", downstream_apis_prefix="Apis")

    builder.run()
