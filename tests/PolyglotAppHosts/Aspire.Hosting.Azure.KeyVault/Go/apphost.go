package main

import (
	"log"

	"apphost/modules/aspire"
)

func main() {
	builder, err := aspire.CreateBuilder(nil)
	if err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	// ── 1. AddAzureKeyVault ──────────────────────────────────────────────────
	vault := builder.AddAzureKeyVault("vault")

	// Parameters for secret-based APIs
	secretParam := builder.AddParameter("secret-param",
		&aspire.AddParameterOptions{Secret: aspire.BoolPtr(true)})
	namedSecretParam := builder.AddParameter("named-secret-param",
		&aspire.AddParameterOptions{Secret: aspire.BoolPtr(true)})

	// Reference expressions for expression-based APIs
	exprSecretValue := aspire.RefExpr("secret-value-%v", secretParam)
	namedExprSecretValue := aspire.RefExpr("named-secret-value-%v", namedSecretParam)

	// ── 2. WithKeyVaultRoleAssignments ────────────────────────────────────────
	vault.WithKeyVaultRoleAssignments(vault, []aspire.AzureKeyVaultRole{
		aspire.AzureKeyVaultRoleKeyVaultReader,
		aspire.AzureKeyVaultRoleKeyVaultSecretsUser,
	})

	// ── 3. AddSecret ──────────────────────────────────────────────────────────
	secretFromParameter := vault.AddSecret("param-secret", secretParam)

	// ── 4. AddSecretFromExpression ────────────────────────────────────────────
	secretFromExpression := vault.AddSecretFromExpression("expr-secret", exprSecretValue)

	// ── 5. AddSecretWithName ──────────────────────────────────────────────────
	namedSecretFromParameter := vault.AddSecretWithName(
		"secret-resource-param", "named-param-secret", namedSecretParam)

	// ── 6. AddSecretWithNameFromExpression ────────────────────────────────────
	namedSecretFromExpression := vault.AddSecretWithNameFromExpression(
		"secret-resource-expr", "named-expr-secret", namedExprSecretValue)

	// ── 7. GetSecret ──────────────────────────────────────────────────────────
	_ = vault.GetSecret("param-secret")

	// Apply role assignments to created secret resources to validate generic coverage.
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
		log.Fatalf(aspire.FormatError(err))
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
}
