// Aspire Go validation AppHost - Aspire.Hosting.Azure.KeyVault
// Mirrors the TypeScript/Python/Java fixture for API surface validation.
// Run `aspire restore --apphost apphost.go` to generate the SDK, then `go build ./...`.
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

	vault, err := builder.AddAzureKeyVault("resource")
	if err != nil {
		log.Fatalf("AddAzureKeyVault: %v", err)
	}
	_, _ = builder.AddParameter("parameter", nil)
	_, _ = builder.AddParameter("parameter", nil)

	_, _ = vault.WithKeyVaultRoleAssignments(vault, nil)

	secretFromParameter, err := vault.AddSecret("resource", nil)
	if err != nil {
		log.Fatalf("AddSecret: %v", err)
	}

	secretFromExpression, err := vault.AddSecretFromExpression("resource", nil)
	if err != nil {
		log.Fatalf("AddSecretFromExpression: %v", err)
	}

	namedSecretFromParameter, err := vault.AddSecretWithName("resource", "resource", nil)
	if err != nil {
		log.Fatalf("AddSecretWithName: %v", err)
	}

	namedSecretFromExpression, err := vault.AddSecretWithNameFromExpression("resource", "resource", nil)
	if err != nil {
		log.Fatalf("AddSecretWithNameFromExpression: %v", err)
	}

	_, _ = vault.GetSecret("resource")

	_, _ = secretFromParameter.WithKeyVaultRoleAssignments(vault, nil)
	_, _ = secretFromExpression.WithKeyVaultRoleAssignments(vault, nil)
	_, _ = namedSecretFromParameter.WithKeyVaultRoleAssignments(vault, nil)
	_, _ = namedSecretFromExpression.WithKeyVaultRoleAssignments(vault, nil)

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
