# Aspire Python validation AppHost
# Mirrors the top-level TypeScript playground surface with Python-style members.

from aspire_app import create_builder


with create_builder() as builder:
    # ── 1. addAzureKeyVault ──────────────────────────────────────────────────────
    vault = builder.add_azure_key_vault("resource")
    # Parameters for secret-based APIs
    secret_param = builder.add_parameter("parameter")
    named_secret_param = builder.add_parameter("parameter")
    # Reference expressions for expression-based APIs
    expr_secret_value = "expression"
    named_expr_secret_value = "expression"
    # ── 2. withRoleAssignments ───────────────────────────────────────────────────
    vault.with_key_vault_role_assignments()
    # ── 3. addSecret ─────────────────────────────────────────────────────────────
    secret_from_parameter = vault.add_secret("resource")
    # ── 4. addSecretFromExpression ───────────────────────────────────────────────
    secret_from_expression = vault.add_secret_from_expression("resource")
    # ── 5. addSecretWithName ─────────────────────────────────────────────────────
    named_secret_from_parameter = vault.add_secret_with_name("resource")
    # ── 6. addSecretWithNameFromExpression ───────────────────────────────────────
    named_secret_from_expression = vault.add_secret_with_name_from_expression("resource")
    # ── 7. getSecret ─────────────────────────────────────────────────────────────
    _existing_secret_ref = vault.get_secret()
    # Apply role assignments to created secret resources to validate generic coverage.
    secret_from_parameter.with_key_vault_role_assignments()
    secret_from_expression.with_key_vault_role_assignments()
    named_secret_from_parameter.with_key_vault_role_assignments()
    named_secret_from_expression.with_key_vault_role_assignments()
    builder.run()
