import { createBuilder } from './.modules/aspire.js';

const builder = await createBuilder();
const maui = await builder.addMauiProject(
    "mauiapp",
    "../../../../AspireWithMaui/AspireWithMaui.MauiClient/AspireWithMaui.MauiClient.csproj"
);

await maui.addWindowsDevice("mauiapp-windows").withOtlpDevTunnel();
await maui.addMacCatalystDevice("mauiapp-maccatalyst").withOtlpDevTunnel();
await maui.addAndroidDevice("mauiapp-android-device", { deviceId: "emulator-5554" }).withOtlpDevTunnel();
await maui.addAndroidEmulator("mauiapp-android-emulator", { emulatorId: "Pixel_9_API_35" }).withOtlpDevTunnel();
await maui.addiOSDevice("mauiapp-ios-device", { deviceId: "00008030-001234567890123A" }).withOtlpDevTunnel();
await maui.addiOSSimulator("mauiapp-ios-simulator", { simulatorId: "E25BBE37-69BA-4720-B6FD-D54C97791E79" }).withOtlpDevTunnel();

await builder.build().run();
