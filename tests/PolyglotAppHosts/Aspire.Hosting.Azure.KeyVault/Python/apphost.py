# Aspire Python validation AppHost
# Mirrors the top-level TypeScript playground surface with Python-style members.

from aspire_app import AzureResourceInfrastructure, ReferenceExpression, create_builder


def configure_key_vault(infrastructure: AzureResourceInfrastructure):
    service = infrastructure.get_key_vault_service()
    properties = service.properties
    bicep = infrastructure.bicep()
    retention = bicep.binary(bicep.integer(20), "Add", bicep.integer(10))
    properties.enable_purge_protection = True
    properties.soft_delete_retention_in_days = retention
    properties.sku.name = "Premium"


with create_builder() as builder:
    # ── 1. addAzureKeyVault ──────────────────────────────────────────────────────
    vault = builder.add_azure_key_vault("vault")
    vault.configure_infrastructure(configure_key_vault)
    # Parameters for secret-based APIs
    secret_param = builder.add_parameter("secret-param")
    named_secret_param = builder.add_parameter("named-secret-param")
    # Reference expressions for expression-based APIs
    expr_secret_value = ReferenceExpression.format_string("{0}", secret_param)
    named_expr_secret_value = ReferenceExpression.format_string("{0}", named_secret_param)
    # ── 2. with_key_vault_role_assignments ───────────────────────────────────────
    vault.with_key_vault_role_assignments(vault, ["KeyVaultReader"])
    # ── 3. addSecret ─────────────────────────────────────────────────────────────
    secret_from_parameter = vault.add_secret("param-secret", secret_param)
    # ── 4. addSecret with a reference expression ─────────────────────────────────
    secret_from_expression = vault.add_secret("expr-secret", expr_secret_value)
    # ── 5. addSecret with an explicit secret name ─────────────────────────────────
    named_secret_from_parameter = vault.add_secret("secret-resource-param", named_secret_param, secret_name="named-param-secret")
    # ── 6. addSecret with an explicit name and reference expression ───────────────
    named_secret_from_expression = vault.add_secret("secret-resource-expr", named_expr_secret_value, secret_name="named-expr-secret")
    # ── 7. getSecret ─────────────────────────────────────────────────────────────
    _existing_secret_ref = vault.get_secret("param-secret")
    # Apply role assignments to created secret resources to validate generic coverage.
    secret_from_parameter.with_key_vault_role_assignments(vault, ["KeyVaultSecretsUser"])
    secret_from_expression.with_key_vault_role_assignments(vault, ["KeyVaultSecretsUser"])
    named_secret_from_parameter.with_key_vault_role_assignments(vault, ["KeyVaultSecretsUser"])
    named_secret_from_expression.with_key_vault_role_assignments(vault, ["KeyVaultSecretsUser"])
    builder.run()
