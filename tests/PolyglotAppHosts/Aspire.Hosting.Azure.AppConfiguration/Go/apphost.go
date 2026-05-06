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

	appConfig := builder.AddAzureAppConfiguration("appconfig")
	if err = appConfig.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	appConfig.WithRoleAssignments(appConfig, []aspire.AzureAppConfigurationRole{
		aspire.AzureAppConfigurationRoleAppConfigurationDataOwner,
		aspire.AzureAppConfigurationRoleAppConfigurationDataReader,
	})

	appConfig.RunAsEmulator(&aspire.RunAsEmulatorOptions{
		ConfigureEmulator: func(emulator aspire.AzureAppConfigurationEmulatorResource) {
			emulator.WithDataBindMount(&aspire.WithDataBindMountOptions{
				Path: aspire.StringPtr(".aace/appconfig"),
			})
			emulator.WithDataVolume(&aspire.WithDataVolumeOptions{
				Name: aspire.StringPtr("appconfig-data"),
			})
			emulator.WithHostPort(8483)
		},
	})
	if err = appConfig.Err(); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	app, err := builder.Build()
	if err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
}
