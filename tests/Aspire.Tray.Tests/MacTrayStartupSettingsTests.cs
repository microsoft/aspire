// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.Versioning;
using System.Xml.Linq;
using Aspire.Tray.Tests.Helpers;

namespace Aspire.Tray.Tests;

public class MacTrayStartupSettingsTests
{
    public static bool SupportsMac => OperatingSystem.IsMacOS();

    [Fact]
    public void RegistrationContainsOnlyTheOneShotCliCommandAndEscapesXml()
    {
        const string cli = "/opt/Aspire \"A & B\"/aspire";
        var xml = XDocument.Parse(MacTrayStartupSettings.Serialize(cli));
        Assert.Equal("plist", xml.Root!.Name.LocalName);
        Assert.Equal("1.0", xml.Root.Attribute("version")!.Value);
        var entries = xml.Root.Element("dict")!.Elements().ToArray();
        Assert.Equal(["key", "string", "key", "array", "key", "true"], entries.Select(entry => entry.Name.LocalName));
        Assert.Equal(["Label", MacTrayStartupSettings.Label, "ProgramArguments", "RunAtLoad"],
            new[] { entries[0].Value, entries[1].Value, entries[2].Value, entries[4].Value });
        Assert.Equal([cli, "tray", "start", "--non-interactive", "--nologo"], entries[3].Elements("string").Select(value => value.Value));
    }

    [Fact]
    public void ReadAndDisableAreSideEffectFreeUntilExplicitEnable()
    {
        using var installation = new TestTrayStartupInstallation();
        var settings = Create(installation);
        Assert.False(settings.Read().Enabled);
        Assert.True(settings.Read().CanEnable);
        Assert.False(settings.SetEnabled(false).Enabled);
        Assert.False(Directory.Exists(installation.LaunchAgents));

        Assert.True(settings.SetEnabled(true).Enabled);
        var path = Path.Combine(installation.LaunchAgents, MacTrayStartupSettings.FileName);
        var original = File.ReadAllText(path);
        Assert.True(settings.SetEnabled(true).Enabled);
        Assert.Equal(original, File.ReadAllText(path));

        var reopened = Create(installation);
        Assert.True(reopened.Read().Enabled);
        Assert.False(reopened.SetEnabled(false).Enabled);
        Assert.Empty(Directory.GetFiles(installation.LaunchAgents));
    }

    [Fact]
    public void DisablingRemainsAvailableWithoutAStableCli()
    {
        using var installation = new TestTrayStartupInstallation();
        Create(installation).SetEnabled(true);
        var options = installation.Options(windows: false) with { StartupCliPath = null };
        var settings = new MacTrayStartupSettings(options, nativeFrontend: false, installation.LaunchAgents);
        var state = settings.Read();
        Assert.True(state.Enabled);
        Assert.False(state.CanEnable);
        Assert.False(settings.SetEnabled(false).Enabled);
    }

    [Fact]
    public void ForeignRegistrationIsPreserved()
    {
        using var installation = new TestTrayStartupInstallation();
        Directory.CreateDirectory(installation.LaunchAgents);
        var path = Path.Combine(installation.LaunchAgents, MacTrayStartupSettings.FileName);
        File.WriteAllText(path, "<plist><dict><key>KeepAlive</key><true/></dict></plist>");
        var original = File.ReadAllText(path);
        var settings = Create(installation);

        Assert.Throws<InvalidOperationException>(settings.Read);
        Assert.Throws<InvalidOperationException>(() => settings.SetEnabled(false));
        Assert.Equal(original, File.ReadAllText(path));
    }

    [Fact]
    public void ExternalReplacementAfterReadIsPreserved()
    {
        using var installation = new TestTrayStartupInstallation();
        var settings = Create(installation);
        settings.SetEnabled(true);
        var path = Path.Combine(installation.LaunchAgents, MacTrayStartupSettings.FileName);
        var changed = MacTrayStartupSettings.Serialize(Path.Combine(installation.Root, "another-cli"));
        File.WriteAllText(path, changed);

        Assert.Throws<InvalidOperationException>(() => settings.SetEnabled(false));
        Assert.Equal(changed, File.ReadAllText(path));
    }

    [Fact(Skip = "Unix permissions and links are checked on macOS.", SkipUnless = nameof(SupportsMac))]
    [SupportedOSPlatform("macos")]
    public void WritesArePrivateWithoutChangingLaunchAgentsPermissions()
    {
        using var installation = new TestTrayStartupInstallation();
        Directory.CreateDirectory(installation.LaunchAgents);
        var permissions = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
        File.SetUnixFileMode(installation.LaunchAgents, permissions);
        Create(installation).SetEnabled(true);

        Assert.Equal(permissions, File.GetUnixFileMode(installation.LaunchAgents));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(Path.Combine(installation.LaunchAgents, MacTrayStartupSettings.FileName)));
    }

    [Fact(Skip = "Symlink creation is checked on macOS.", SkipUnless = nameof(SupportsMac))]
    public void SymlinkedRegistrationCannotBeReadOrOverwritten()
    {
        using var installation = new TestTrayStartupInstallation();
        Directory.CreateDirectory(installation.LaunchAgents);
        var outside = Path.Combine(installation.Root, "unrelated.plist");
        File.WriteAllText(outside, "unrelated");
        File.CreateSymbolicLink(Path.Combine(installation.LaunchAgents, MacTrayStartupSettings.FileName), outside);

        Assert.Throws<InvalidOperationException>(() => Create(installation).SetEnabled(true));
        Assert.Equal("unrelated", File.ReadAllText(outside));
    }

    private static MacTrayStartupSettings Create(TestTrayStartupInstallation installation)
        => new(installation.Options(windows: false), nativeFrontend: true, installation.LaunchAgents);
}
