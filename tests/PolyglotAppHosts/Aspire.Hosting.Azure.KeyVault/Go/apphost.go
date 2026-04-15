package main

import (
	"log"

	"apphost/modules/aspire"
)

func main() {
	builder, err := aspire.CreateBuilder(nil)
	if err != nil {
		log.Fatalf("CreateBuilder: %v", err)
	}

	// ── 1. addAzureKeyVault ──────────────────────────────────────────────────
	vault := builder.AddAzureKeyVault("vault")

	// ── 2. addParameter (secret params) ──────────────────────────────────────
	secretParam := builder.AddParameterWithOpts("secret-param",
		&aspire.AddParameterOptions{Secret: aspire.BoolPtr(true)})
	namedSecretParam := builder.AddParameterWithOpts("named-secret-param",
		&aspire.AddParameterOptions{Secret: aspire.BoolPtr(true)})

	// ── 3. withKeyVaultRoleAssignments ────────────────────────────────────────
	vault.WithKeyVaultRoleAssignments(vault, []aspire.AzureKeyVaultRole{
		aspire.AzureKeyVaultRoleKeyVaultReader,
		aspire.AzureKeyVaultRoleKeyVaultSecretsUser,
	})

	// ── 4. addSecret ─────────────────────────────────────────────────────────
	secretFromParameter := vault.AddSecret("param-secret", secretParam)

	// ── 5. addSecretFromExpression ────────────────────────────────────────────
	secretFromExpression := vault.AddSecretFromExpression("expr-secret",
		aspire.RefExpr("secret-value-%s", secretParam))

	// ── 6. addSecretWithName ──────────────────────────────────────────────────
	namedSecretFromParameter := vault.AddSecretWithName(
		"secret-resource-param", "named-param-secret", namedSecretParam)

	// ── 7. addSecretWithNameFromExpression ────────────────────────────────────
	namedSecretFromExpression := vault.AddSecretWithNameFromExpression(
		"secret-resource-expr", "named-expr-secret",
		aspire.RefExpr("named-secret-value"))

	// ── 8. getSecret ──────────────────────────────────────────────────────────
	_, _ = vault.GetSecret("param-secret")

	// ── 9. role assignments on each secret resource ───────────────────────────
	secretFromParameter.WithKeyVaultRoleAssignments(vault,
		[]aspire.AzureKeyVaultRole{aspire.AzureKeyVaultRoleKeyVaultSecretsUser})
	secretFromExpression.WithKeyVaultRoleAssignments(vault,
		[]aspire.AzureKeyVaultRole{aspire.AzureKeyVaultRoleKeyVaultReader})
	namedSecretFromParameter.WithKeyVaultRoleAssignments(vault,
		[]aspire.AzureKeyVaultRole{aspire.AzureKeyVaultRoleKeyVaultSecretsOfficer})
	namedSecretFromExpression.WithKeyVaultRoleAssignments(vault,
		[]aspire.AzureKeyVaultRole{aspire.AzureKeyVaultRoleKeyVaultReader})

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
