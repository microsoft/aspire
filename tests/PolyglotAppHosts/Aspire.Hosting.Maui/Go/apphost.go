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

	maui := builder.AddMauiProject("mauiapp", "../../../../AspireWithMaui/AspireWithMaui.MauiClient/AspireWithMaui.MauiClient.csproj")
	maui.AddWindowsDevice("mauiapp-windows").WithOtlpDevTunnel()
	maui.AddMacCatalystDevice("mauiapp-maccatalyst").WithOtlpDevTunnel()
	maui.AddAndroidDevice("mauiapp-android-device", &aspire.AddAndroidDeviceOptions{DeviceId: aspire.StringPtr("emulator-5554")}).WithOtlpDevTunnel()
	maui.AddAndroidEmulator("mauiapp-android-emulator", &aspire.AddAndroidEmulatorOptions{EmulatorId: aspire.StringPtr("Pixel_9_API_35")}).WithOtlpDevTunnel()
	maui.AddiOSDevice("mauiapp-ios-device", &aspire.AddiOSDeviceOptions{DeviceId: aspire.StringPtr("00008030-001234567890123A")}).WithOtlpDevTunnel()
	maui.AddiOSSimulator("mauiapp-ios-simulator", &aspire.AddiOSSimulatorOptions{SimulatorId: aspire.StringPtr("E25BBE37-69BA-4720-B6FD-D54C97791E79")}).WithOtlpDevTunnel()
	if err = maui.Err(); err != nil {
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
