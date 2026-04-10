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
	_, _ = builder.AddParameter("parameter")
	_, _ = builder.AddParameter("parameter")

	_, _ = vault.WithKeyVaultRoleAssignments()

	secretFromParameter, err := vault.AddSecret("resource")
	if err != nil {
		log.Fatalf("AddSecret: %v", err)
	}

	secretFromExpression, err := vault.AddSecretFromExpression("resource")
	if err != nil {
		log.Fatalf("AddSecretFromExpression: %v", err)
	}

	namedSecretFromParameter, err := vault.AddSecretWithName("resource")
	if err != nil {
		log.Fatalf("AddSecretWithName: %v", err)
	}

	namedSecretFromExpression, err := vault.AddSecretWithNameFromExpression("resource")
	if err != nil {
		log.Fatalf("AddSecretWithNameFromExpression: %v", err)
	}

	_, _ = vault.GetSecret()

	_, _ = secretFromParameter.WithKeyVaultRoleAssignments()
	_, _ = secretFromExpression.WithKeyVaultRoleAssignments()
	_, _ = namedSecretFromParameter.WithKeyVaultRoleAssignments()
	_, _ = namedSecretFromExpression.WithKeyVaultRoleAssignments()

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
