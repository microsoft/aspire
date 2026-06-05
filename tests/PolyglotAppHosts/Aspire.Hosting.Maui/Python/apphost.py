# Aspire Python validation AppHost
# Mirrors the top-level TypeScript playground surface with Python-style members.

from aspire_app import create_builder


with create_builder() as builder:
    maui = builder.add_maui_project("resource")
    maui.add_windows_device("resource")
    maui.add_mac_catalyst_device("resource")
    maui.add_android_device("resource")
    maui.add_android_emulator("resource")
    maui.addi_osdevice()
    maui.addi_ossimulator()
    builder.run()
