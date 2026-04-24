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
await vault.withRoleAssignments(vault, [
    AzureKeyVaultRole.KeyVaultReader,
    AzureKeyVaultRole.KeyVaultSecretsUser,
]);

// ── 3. addSecret ─────────────────────────────────────────────────────────────
const secretFromParameter = await vault.addSecret("param-secret", secretParam);

// ── 4. addSecretFromExpression ───────────────────────────────────────────────
const secretFromExpression = await vault.addSecret("expr-secret", exprSecretValue);

// ── 5. addSecretWithName ─────────────────────────────────────────────────────
const namedSecretFromParameter = await vault.addSecret("secret-resource-param", namedSecretParam, { secretName: "named-param-secret" });

// ── 6. addSecretWithNameFromExpression ───────────────────────────────────────
const namedSecretFromExpression = await vault.addSecret("secret-resource-expr", namedExprSecretValue, { secretName: "named-expr-secret" });

// ── 7. getSecret ─────────────────────────────────────────────────────────────
const _existingSecretRef = await vault.getSecret("param-secret");

// Apply role assignments to created secret resources to validate generic coverage.
await secretFromParameter.withRoleAssignments(vault, [AzureKeyVaultRole.KeyVaultSecretsUser]);
await secretFromExpression.withRoleAssignments(vault, [AzureKeyVaultRole.KeyVaultReader]);
await namedSecretFromParameter.withRoleAssignments(vault, [AzureKeyVaultRole.KeyVaultSecretsOfficer]);
await namedSecretFromExpression.withRoleAssignments(vault, [AzureKeyVaultRole.KeyVaultReader]);

await builder.build().run();
