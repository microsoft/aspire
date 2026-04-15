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

	// ── 1. addAzurePostgres ───────────────────────────────────────────────────
	pg := builder.AddAzurePostgresFlexibleServer("pg")
	_ = pg.AddDatabase("mydb")

	// ── 2. withPasswordAuthentication ────────────────────────────────────────
	pgAuth := builder.AddAzurePostgresFlexibleServer("pg-auth")
	pgAuth.WithPasswordAuthentication()

	// ── 3. withPasswordAuthenticationWithKeyVault ─────────────────────────────
	kv := builder.AddAzureKeyVault("kv")
	pgAuth.WithPasswordAuthenticationWithKeyVault(
		aspire.NewIAzureKeyVaultResource(kv.Handle(), kv.Client()),
	)

	// ── 4. runAsContainer ─────────────────────────────────────────────────────
	pgEmulator := builder.AddAzurePostgresFlexibleServer("pg-emulator")
	pgEmulator.RunAsContainer(nil)

	// ── 5. property accessors ─────────────────────────────────────────────────
	_, _ = pg.GetConnectionProperty("connectionString")
	_, _ = pg.GetConnectionProperty("host")
	_, _ = pg.GetConnectionProperty("port")
	_, _ = pg.GetConnectionProperty("username")
	_, _ = pgAuth.GetConnectionProperty("password")

	if err = pg.Err(); err != nil {
		log.Fatalf("pg: %v", err)
	}
	if err = pgAuth.Err(); err != nil {
		log.Fatalf("pgAuth: %v", err)
	}
	if err = pgEmulator.Err(); err != nil {
		log.Fatalf("pgEmulator: %v", err)
	}

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
