import aspire.*;

void main() throws Exception {
        var builder = DistributedApplication.CreateBuilder();
        var maui = builder.addMauiProject(
            "mauiapp",
            "../../../../AspireWithMaui/AspireWithMaui.MauiClient/AspireWithMaui.MauiClient.csproj"
        );
        maui.addWindowsDevice("mauiapp-windows").withOtlpDevTunnel();
        maui.addMacCatalystDevice("mauiapp-maccatalyst").withOtlpDevTunnel();
        maui.addAndroidDevice("mauiapp-android-device", "emulator-5554").withOtlpDevTunnel();
        maui.addAndroidEmulator("mauiapp-android-emulator", "Pixel_9_API_35").withOtlpDevTunnel();
        maui.addiOSDevice("mauiapp-ios-device", "00008030-001234567890123A").withOtlpDevTunnel();
        maui.addiOSSimulator("mauiapp-ios-simulator", "E25BBE37-69BA-4720-B6FD-D54C97791E79").withOtlpDevTunnel();
        builder.build().run();
    }
