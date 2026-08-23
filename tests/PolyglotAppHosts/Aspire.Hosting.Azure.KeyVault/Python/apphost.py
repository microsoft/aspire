# Aspire Python validation AppHost
# Mirrors the top-level TypeScript playground surface with Python-style members.

from aspire_app import ReferenceExpression, create_builder


with create_builder() as builder:
    # ── 1. addAzureKeyVault ──────────────────────────────────────────────────────
    vault = builder.add_azure_key_vault("resource")
    # Parameters for secret-based APIs
    secret_param = builder.add_parameter("parameter")
    named_secret_param = builder.add_parameter("parameter")
    # Reference expressions for expression-based APIs
    expr_secret_value = ReferenceExpression.format_string("{0}", secret_param)
    named_expr_secret_value = ReferenceExpression.format_string("{0}", named_secret_param)
    # ── 2. with_key_vault_role_assignments ───────────────────────────────────────
    vault.with_key_vault_role_assignments(vault, ["KeyVaultReader"])
    # ── 3. addSecret ─────────────────────────────────────────────────────────────
    secret_from_parameter = vault.add_secret("resource", secret_param)
    # ── 4. addSecret with a reference expression ─────────────────────────────────
    secret_from_expression = vault.add_secret("resource", expr_secret_value)
    # ── 5. addSecret with an explicit secret name ─────────────────────────────────
    named_secret_from_parameter = vault.add_secret("resource", named_secret_param, secret_name="secret")
    # ── 6. addSecret with an explicit name and reference expression ───────────────
    named_secret_from_expression = vault.add_secret("resource", named_expr_secret_value, secret_name="secret")
    # ── 7. getSecret ─────────────────────────────────────────────────────────────
    _existing_secret_ref = vault.get_secret("secret")
    # Apply role assignments to created secret resources to validate generic coverage.
    secret_from_parameter.with_key_vault_role_assignments(vault, ["KeyVaultSecretsUser"])
    secret_from_expression.with_key_vault_role_assignments(vault, ["KeyVaultSecretsUser"])
    named_secret_from_parameter.with_key_vault_role_assignments(vault, ["KeyVaultSecretsUser"])
    named_secret_from_expression.with_key_vault_role_assignments(vault, ["KeyVaultSecretsUser"])
    builder.run()
