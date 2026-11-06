// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Tray.Tests;

public class LinuxPlasmaAnchorTests
{
    [Theory]
    [InlineData("KDE", "wayland", true)]
    [InlineData("plasma:KDE", "WAYLAND", true)]
    [InlineData("KDE", "x11", false)]
    [InlineData("GNOME", "wayland", false)]
    [InlineData("sway", "wayland", false)]
    [InlineData(null, null, false)]
    public void OnlyPlasmaWaylandNeedsItsNativeCoordinateMapping(string? desktop, string? session, bool expected)
    {
        Assert.Equal(expected, LinuxPlasmaAnchor.IsRequired(desktop, session));
    }

    [Fact]
    public void OutputInformationPreservesNamesNegativeOriginsAndFractionalScales()
    {
        var outputs = LinuxPlasmaAnchor.ParseOutputs("""
            KWin Support Information
            Scale: unrelated
            Screens
            =======
            Number of Screens: 3

            Screen 0:
            ---------
            Name: eDP-1
            Enabled: 1
            Geometry: 0,0,1920x1280
            Physical size: 285x190mm
            Scale: 1.5

            Screen 1:
            ---------
            Name: DP-5
            Enabled: 1
            Geometry: -3072,-100,3072x1728
            Scale: 1.25

            Screen 2:
            ---------
            Name: HDMI-1
            Enabled: 0

            Compositing
            ===========
            Name: unrelated
            Scale: unrelated
            """);

        Assert.Equal([
            new LinuxPlasmaAnchor.Output("eDP-1", new(0, 0, 1920, 1280), 1.5),
            new LinuxPlasmaAnchor.Output("DP-5", new(-3072, -100, 3072, 1728), 1.25)
        ], outputs);
    }

    [Theory]
    [InlineData("Screens\nScreen 0:\nName: DP-5\nEnabled: 1\nScale: 1.25")]
    [InlineData("Screens\nScreen 0:\nName: DP-5\nEnabled: 1\nGeometry: 0,0,1000x800\nScale: NaN")]
    [InlineData("Screens\nScreen 0:\nName: DP-5\nEnabled: 1\nGeometry: 0,0,1000x800\nScale: 0")]
    [InlineData("Screens\nScreen 0:\nName: DP-5\nEnabled: 1\nGeometry: malformed\nScale: 1")]
    [InlineData("Screens\nScreen 0:\nName: DP-5\nEnabled: 1\nGeometry: 0,0,0x800\nScale: 1")]
    [InlineData("Screens\nScreen 0:\nName: DP-5\nEnabled: 0")]
    [InlineData("Unrelated information")]
    public void InvalidOutputInformationFailsExplicitly(string information)
    {
        Assert.Throws<InvalidOperationException>(() => LinuxPlasmaAnchor.ParseOutputs(information));
    }

    [Fact]
    public void ExternalPrimaryIconMapsToXWaylandCoordinatesInsteadOfTheScreenMiddle()
    {
        LinuxPlasmaAnchor.Output[] outputs = [
            new("eDP-1", new(0, 0, 1920, 1280), 1.5),
            new("DP-5", new(1920, 0, 3072, 1728), 1.25)
        ];
        LinuxPlasmaAnchor.Monitor[] monitors = [
            new("eDP-1", new(0, 0, 2880, 1920), false),
            new("DP-5", new(2880, 0, 4608, 2592), true)
        ];

        var anchor = LinuxPlasmaAnchor.Map(new(5735, 20, 0, 0), outputs, monitors, true, pointer: null);

        Assert.Equal(new PanelRect(7458, 24, 0, 0), anchor);
        var panel = LinuxTrayPanel.Place(monitors[1].Geometry, anchor.CenterX, anchor.Bottom, 200, 1.5);
        Assert.Equal(monitors[1].Geometry.Right - 6, panel.Right);
    }

    [Fact]
    public void WaylandAnchorMapsNativePixelsBackToLogicalOutputCoordinates()
    {
        LinuxPlasmaAnchor.Output[] outputs = [
            new("eDP-1", new(0, 0, 1920, 1280), 1.5),
            new("DP-5", new(1920, 0, 3072, 1728), 1.25)
        ];
        LinuxPlasmaAnchor.Monitor[] monitors = [new("GTK model name", new(1920, 0, 3072, 1728), true)];

        var anchor = LinuxPlasmaAnchor.Map(new(5735, 20, 0, 0), outputs, monitors, true, pointer: null);

        Assert.Equal(new PanelRect(4972, 16, 0, 0), anchor);
    }

    [Theory]
    [InlineData(true, 100, 300, 2976)]
    [InlineData(false, 100, 300, 2000)]
    [InlineData(true, 2000, 20, 2000)]
    [InlineData(false, 2976, 24, 2976)]
    public void OverlappingNativeOutputsRespectMouseOrKeyboardActivation(bool externalPrimary,
        double pointerX, double pointerY, double expectedX)
    {
        LinuxPlasmaAnchor.Output[] outputs = [
            new("eDP-1", new(0, 0, 1920, 1280), 1.5),
            new("DP-5", new(1920, 0, 3072, 1728), 1.25)
        ];
        LinuxPlasmaAnchor.Monitor[] monitors = [
            new("eDP-1", new(0, 0, 2880, 1920), !externalPrimary),
            new("DP-5", new(2880, 0, 4608, 2592), externalPrimary)
        ];
        var anchor = LinuxPlasmaAnchor.Map(new(2000, 20, 0, 0), outputs, monitors, true, (pointerX, pointerY));

        Assert.Equal(expectedX, anchor.X);
    }

    [Fact]
    public void NegativeOutputOriginsMapRelativeToTheCorrectMonitor()
    {
        LinuxPlasmaAnchor.Output[] outputs = [new("DP-5", new(-1920, -100, 1920, 1080), 1.25)];
        LinuxPlasmaAnchor.Monitor[] monitors = [new("DP-5", new(-2880, -150, 2880, 1620), true)];

        var anchor = LinuxPlasmaAnchor.Map(new(-2380, -105, 0, 0), outputs, monitors, true, pointer: null);

        Assert.Equal(new PanelRect(-2856, -126, 0, 0), anchor);
    }

    [Fact]
    public void OutputMappingDoesNotRequireTheMouseToStayOverTheTrayIcon()
    {
        LinuxPlasmaAnchor.Output[] outputs = [
            new("eDP-1", new(0, 0, 1920, 1280), 1.5),
            new("DP-5", new(1920, 0, 3072, 1728), 1.25)
        ];
        LinuxPlasmaAnchor.Monitor[] monitors = [new("DP-5", new(2880, 0, 4608, 2592), true)];

        var anchor = LinuxPlasmaAnchor.Map(new(5735, 20, 0, 0), outputs, monitors, true, pointer: (100, 100));

        Assert.Equal(new PanelRect(7458, 24, 0, 0), anchor);
    }

    [Theory]
    [InlineData("[KScreen]\nXwaylandClientsScale=true", true)]
    [InlineData("[KScreen]\nXwaylandClientsScale=false", false)]
    [InlineData("[KScreen][$i]\nXwaylandClientsScale[$i]=false", false)]
    [InlineData("[Unrelated]\nXwaylandClientsScale=false", null)]
    [InlineData("[KScreen]\nOtherSetting=false", null)]
    public void PlasmaScalingPreferenceSelectsItsCoordinateSystem(string configuration, bool? expected)
    {
        using var reader = new StringReader(configuration);
        Assert.Equal(expected, LinuxPlasmaAnchor.ParseNativeCoordinates(reader));
    }

    [Fact]
    public void InvalidPlasmaScalingPreferenceFailsExplicitly()
    {
        using var reader = new StringReader("[KScreen]\nXwaylandClientsScale=invalid");
        Assert.Throws<InvalidOperationException>(() => LinuxPlasmaAnchor.ParseNativeCoordinates(reader));
    }

    [Theory]
    [InlineData(1, 4972, 16)]
    [InlineData(1.5, 7458, 24)]
    public void UnscaledPlasmaCoordinatesMapToTheGtkOutput(double gtkScale, double expectedX, double expectedY)
    {
        LinuxPlasmaAnchor.Output[] outputs = [new("DP-5", new(1920, 0, 3072, 1728), 1.25)];
        LinuxPlasmaAnchor.Monitor[] monitors = [
            new("DP-5", new(1920 * gtkScale, 0, 3072 * gtkScale, 1728 * gtkScale), true)
        ];

        var anchor = LinuxPlasmaAnchor.Map(new(4972, 16, 0, 0), outputs, monitors, false, pointer: null);

        Assert.Equal(new PanelRect(expectedX, expectedY, 0, 0), anchor);
    }
}
