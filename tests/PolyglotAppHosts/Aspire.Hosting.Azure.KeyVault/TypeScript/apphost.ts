// Aspire TypeScript AppHost — Azure Key Vault validation
// Exercises every exported member of Aspire.Hosting.Azure.KeyVault
import { AzureKeyVaultRole, createBuilder, refExpr } from './.modules/aspire.js';

const builder = await createBuilder();

// ── 1. addAzureKeyVault ──────────────────────────────────────────────────────
const vault = await builder.addAzureKeyVault("vault");

// Parameters for secret-based APIs
const secretParam = await builder.addParameter("secret-param", { secret: true });
const namedSecretParam = await builder.addParameter("named-secret-param", { secret: true });

// Reference expressions for expression-based APIs
const exprSecretValue = refExpr`secret-value-${secretParam}`;
const namedExprSecretValue = refExpr`named-secret-value-${namedSecretParam}`;

// ── 2. withRoleAssignments ───────────────────────────────────────────────────
await vault.withKeyVaultRoleAssignments(vault, [
    AzureKeyVaultRole.KeyVaultReader,
    AzureKeyVaultRole.KeyVaultSecretsUser,
]);

// ── 3. addSecret ─────────────────────────────────────────────────────────────
const secretFromParameter = await vault.addSecret("param-secret", secretParam);

// ── 4. addSecretFromExpression ───────────────────────────────────────────────
const secretFromExpression = await vault.addSecretFromExpression("expr-secret", exprSecretValue);

// ── 5. addSecretWithName ─────────────────────────────────────────────────────
const namedSecretFromParameter = await vault.addSecretWithName("secret-resource-param", "named-param-secret", namedSecretParam);

// ── 6. addSecretWithNameFromExpression ───────────────────────────────────────
const namedSecretFromExpression = await vault.addSecretWithNameFromExpression("secret-resource-expr", "named-expr-secret", namedExprSecretValue);

// ── 7. getSecret ─────────────────────────────────────────────────────────────
const _existingSecretRef = await vault.getSecret("param-secret");

// Apply role assignments to created secret resources to validate generic coverage.
await secretFromParameter.withKeyVaultRoleAssignments(vault, [AzureKeyVaultRole.KeyVaultSecretsUser]);
await secretFromExpression.withKeyVaultRoleAssignments(vault, [AzureKeyVaultRole.KeyVaultReader]);
await namedSecretFromParameter.withKeyVaultRoleAssignments(vault, [AzureKeyVaultRole.KeyVaultSecretsOfficer]);
await namedSecretFromExpression.withKeyVaultRoleAssignments(vault, [AzureKeyVaultRole.KeyVaultReader]);

await builder.build().run();
