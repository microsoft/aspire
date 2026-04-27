import aspire.*;

void main() throws Exception {
        // Aspire TypeScript AppHost - Azure Key Vault validation
        // Exercises every exported member of Aspire.Hosting.Azure.KeyVault
        var builder = DistributedApplication.CreateBuilder();
        // ── 1. addAzureKeyVault ──────────────────────────────────────────────────────
        var vault = builder.addAzureKeyVault("vault");
        // Parameters for secret-based APIs
        var secretParam = builder.addParameter("secret-param", new AddParameterOptions().secret(true));
        var namedSecretParam = builder.addParameter("named-secret-param", new AddParameterOptions().secret(true));
        // Reference expressions for expression-based APIs
        var exprSecretValue = ReferenceExpression.refExpr("secret-value-%s", secretParam);
        var namedExprSecretValue = ReferenceExpression.refExpr("named-secret-value-%s", namedSecretParam);
        // ── 2. withRoleAssignments ───────────────────────────────────────────────────
        vault.withRoleAssignments(vault, new AzureKeyVaultRole[] { AzureKeyVaultRole.KEY_VAULT_READER, AzureKeyVaultRole.KEY_VAULT_SECRETS_USER });
        // ── 3. addSecret ─────────────────────────────────────────────────────────────
        var secretFromParameter = vault.addSecret("param-secret", secretParam, null);
        // ── 4. addSecretFromExpression ───────────────────────────────────────────────
        var secretFromExpression = vault.addSecret("expr-secret", exprSecretValue, null);
        // ── 5. addSecretWithName ─────────────────────────────────────────────────────
        var namedSecretFromParameter = vault.addSecret("secret-resource-param", namedSecretParam, "named-param-secret");
        // ── 6. addSecretWithNameFromExpression ───────────────────────────────────────
        var namedSecretFromExpression = vault.addSecret("secret-resource-expr", namedExprSecretValue, "named-expr-secret");
        // ── 7. getSecret ─────────────────────────────────────────────────────────────
        var _existingSecretRef = vault.getSecret("param-secret");
        // Apply role assignments to created secret resources to validate generic coverage.
        secretFromParameter.withRoleAssignments(vault, new AzureKeyVaultRole[] { AzureKeyVaultRole.KEY_VAULT_SECRETS_USER });
        secretFromExpression.withRoleAssignments(vault, new AzureKeyVaultRole[] { AzureKeyVaultRole.KEY_VAULT_READER });
        namedSecretFromParameter.withRoleAssignments(vault, new AzureKeyVaultRole[] { AzureKeyVaultRole.KEY_VAULT_SECRETS_OFFICER });
        namedSecretFromExpression.withRoleAssignments(vault, new AzureKeyVaultRole[] { AzureKeyVaultRole.KEY_VAULT_READER });
        builder.build().run();
    }
