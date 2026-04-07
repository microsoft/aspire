import aspire.*;

void main() throws Exception {
        // Aspire TypeScript AppHost - Azure Key Vault validation
        // Exercises every exported member of Aspire.Hosting.Azure.KeyVault
        var builder = DistributedApplication.CreateBuilder();
        // ── 1. addAzureKeyVault ──────────────────────────────────────────────────────
        var vault = builder.addAzureKeyVault("vault");
        // Parameters for secret-based APIs
        var secretParam = builder.addParameter("secret-param", true);
        var namedSecretParam = builder.addParameter("named-secret-param", true);
        // Reference expressions for expression-based APIs
        var exprSecretValue = ReferenceExpression.refExpr("secret-value-%s", secretParam);
        var namedExprSecretValue = ReferenceExpression.refExpr("named-secret-value-%s", namedSecretParam);
        // ── 2. withRoleAssignments ───────────────────────────────────────────────────
        vault.withKeyVaultRoleAssignments(vault, new AzureKeyVaultRole[] { AzureKeyVaultRole.KEY_VAULT_READER, AzureKeyVaultRole.KEY_VAULT_SECRETS_USER });
        // ── 3. addSecret ─────────────────────────────────────────────────────────────
        var secretFromParameter = vault.addSecret("param-secret", secretParam);
        // ── 4. addSecretFromExpression ───────────────────────────────────────────────
        var secretFromExpression = vault.addSecretFromExpression("expr-secret", exprSecretValue);
        // ── 5. addSecretWithName ─────────────────────────────────────────────────────
        var namedSecretFromParameter = vault.addSecretWithName("secret-resource-param", "named-param-secret", namedSecretParam);
        // ── 6. addSecretWithNameFromExpression ───────────────────────────────────────
        var namedSecretFromExpression = vault.addSecretWithNameFromExpression("secret-resource-expr", "named-expr-secret", namedExprSecretValue);
        // ── 7. getSecret ─────────────────────────────────────────────────────────────
        var _existingSecretRef = vault.getSecret("param-secret");
        // Apply role assignments to created secret resources to validate generic coverage.
        secretFromParameter.withKeyVaultRoleAssignments(vault, new AzureKeyVaultRole[] { AzureKeyVaultRole.KEY_VAULT_SECRETS_USER });
        secretFromExpression.withKeyVaultRoleAssignments(vault, new AzureKeyVaultRole[] { AzureKeyVaultRole.KEY_VAULT_READER });
        namedSecretFromParameter.withKeyVaultRoleAssignments(vault, new AzureKeyVaultRole[] { AzureKeyVaultRole.KEY_VAULT_SECRETS_OFFICER });
        namedSecretFromExpression.withKeyVaultRoleAssignments(vault, new AzureKeyVaultRole[] { AzureKeyVaultRole.KEY_VAULT_READER });
        builder.build().run();
    }
