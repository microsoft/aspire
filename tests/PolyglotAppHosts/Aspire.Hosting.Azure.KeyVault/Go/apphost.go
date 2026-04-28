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

	// ── 2. WithRoleAssignments ────────────────────────────────────────────────
	vault.WithRoleAssignments(vault, []aspire.AzureKeyVaultRole{
		aspire.AzureKeyVaultRoleKeyVaultReader,
		aspire.AzureKeyVaultRoleKeyVaultSecretsUser,
	})

	// ── 3. AddSecret ──────────────────────────────────────────────────────────
	secretFromParameter := vault.AddSecret("param-secret", secretParam)

	// ── 4. AddSecret (expression value) ───────────────────────────────────────
	secretFromExpression := vault.AddSecret("expr-secret", exprSecretValue)

	// ── 5. AddSecret (custom secret name) ─────────────────────────────────────
	namedSecretFromParameter := vault.AddSecret(
		"secret-resource-param", namedSecretParam, &aspire.AddSecretOptions{SecretName: aspire.StringPtr("named-param-secret")})

	// ── 6. AddSecret (custom secret name and expression value) ────────────────
	namedSecretFromExpression := vault.AddSecret(
		"secret-resource-expr", namedExprSecretValue, &aspire.AddSecretOptions{SecretName: aspire.StringPtr("named-expr-secret")})

	// ── 7. GetSecret ──────────────────────────────────────────────────────────
	_ = vault.GetSecret("param-secret")

	// Apply role assignments to created secret resources to validate generic coverage.
	secretFromParameter.WithRoleAssignments(vault,
		[]aspire.AzureKeyVaultRole{aspire.AzureKeyVaultRoleKeyVaultSecretsUser})
	secretFromExpression.WithRoleAssignments(vault,
		[]aspire.AzureKeyVaultRole{aspire.AzureKeyVaultRoleKeyVaultReader})
	namedSecretFromParameter.WithRoleAssignments(vault,
		[]aspire.AzureKeyVaultRole{aspire.AzureKeyVaultRoleKeyVaultSecretsOfficer})
	namedSecretFromExpression.WithRoleAssignments(vault,
		[]aspire.AzureKeyVaultRole{aspire.AzureKeyVaultRoleKeyVaultReader})

	app, err := builder.Build()
	if err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
}
