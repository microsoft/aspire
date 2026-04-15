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

	// ── 1. addAzureAppConfiguration ──────────────────────────────────────────
	appConfig := builder.AddAzureAppConfiguration("appconfig")

	// ── 2. withAppConfigurationRoleAssignments ────────────────────────────────
	appConfig.WithAppConfigurationRoleAssignments(appConfig, []aspire.AzureAppConfigurationRole{
		aspire.AzureAppConfigurationRoleAppConfigurationDataOwner,
		aspire.AzureAppConfigurationRoleAppConfigurationDataReader,
	})

	// ── 3. runAsEmulator (typed callback) ─────────────────────────────────────
	appConfig.RunAsEmulator(func(emulator *aspire.AzureAppConfigurationEmulatorResource) {
		emulator.WithDataBindMountWithOpts(&aspire.WithDataBindMountOptions{
			Path: aspire.StringPtr(".aace/appconfig"),
		})
		emulator.WithDataVolumeWithOpts(&aspire.WithDataVolumeOptions{
			Name: aspire.StringPtr("appconfig-data"),
		})
		emulator.WithHostPort(8483)
	})
	if err = appConfig.Err(); err != nil {
		log.Fatalf("appConfig: %v", err)
	}

	app, err := builder.Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf("Run: %v", err)
	}
}
