// Aspire TypeScript AppHost — Azure Key Vault validation
// Exercises the Key Vault resource and opt-in provisioning exports.
import { AzureKeyVaultRole, BinaryBicepOperator, KeyVaultSkuName, createBuilder, refExpr } from './.aspire/modules/aspire.mjs';

const builder = await createBuilder();

// ── 1. addAzureKeyVault ──────────────────────────────────────────────────────
const vault = await builder.addAzureKeyVault("vault");

await vault.configureInfrastructure(async infrastructure => {
    const service = await infrastructure.getKeyVaultService();
    const properties = await service.properties.get();
    const sku = await properties.sku.get();
    const bicep = infrastructure.bicep();
    const retention = bicep.binary(
        bicep.integer(20),
        BinaryBicepOperator.Add,
        bicep.integer(10));
    const publicNetworkAccess = bicep.asString(bicep.string("Enabled"));

    await properties.enablePurgeProtection.set(true);
    await properties.publicNetworkAccess.set(publicNetworkAccess);
    await properties.softDeleteRetentionInDays.set(retention);
    await sku.name.set(KeyVaultSkuName.Premium);
});

// Parameters for secret-based APIs
const secretParam = await builder.addParameter("secret-param", { secret: true });
const namedSecretParam = await builder.addParameter("named-secret-param", { secret: true });

// Reference expressions for expression-based APIs
const exprSecretValue = refExpr`secret-value-${secretParam}`;
const namedExprSecretValue = refExpr`named-secret-value-${namedSecretParam}`;

// ── 2. withKeyVaultRoleAssignments ───────────────────────────────────────────
await vault.withKeyVaultRoleAssignments(vault, [
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
await secretFromParameter.withKeyVaultRoleAssignments(vault, [AzureKeyVaultRole.KeyVaultSecretsUser]);
await secretFromExpression.withKeyVaultRoleAssignments(vault, [AzureKeyVaultRole.KeyVaultReader]);
await namedSecretFromParameter.withKeyVaultRoleAssignments(vault, [AzureKeyVaultRole.KeyVaultSecretsOfficer]);
await namedSecretFromExpression.withKeyVaultRoleAssignments(vault, [AzureKeyVaultRole.KeyVaultReader]);

await builder.build().run();
