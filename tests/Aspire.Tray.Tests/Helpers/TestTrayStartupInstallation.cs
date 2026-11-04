// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using Aspire.Tests.Utils;

namespace Aspire.Tray.Tests.Helpers;

internal sealed class TestTrayStartupInstallation : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("ats-");

    public string Root { get; }
    public string BundleRoot => Path.Combine(Root, "versions", "test-version");
    public string MacCli => Path.Combine(Root, "aspire mac");
    public string WindowsCli => Path.Combine(Root, "aspire.exe");
    public string SourceGui => Path.Combine(BundleRoot, "tray", "aspire-tray.exe");
    public string Bootstrap => Path.Combine(Root, "state", "aspire-tray-login.exe");
    public string LaunchAgents => Path.Combine(Root, "isolated home", "Library", "LaunchAgents");

    public TestTrayStartupInstallation()
    {
        Root = Path.Combine(_directory.FullName, "i & s");
        Directory.CreateDirectory(BundleRoot);
        Span<byte> header = stackalloc byte[16];
        header.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0xfeedfacf);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], 2);
        File.WriteAllBytes(MacCli, header.ToArray());
        WindowsTrayTestPayload.Create(Path.GetDirectoryName(SourceGui)!, "win-x64");
        File.Copy(SourceGui, WindowsCli);
        using var stream = File.OpenWrite(WindowsCli);
        stream.Position = 0x80 + 24 + 68;
        stream.WriteByte((byte)Subsystem.WindowsCui);
    }

    public TrayOptions Options(bool windows)
    {
        var cli = windows ? WindowsCli : MacCli;
        return new(cli, null, BundleRoot, cli);
    }

    public WindowsTrayStartupSettings WindowsSettings(ITrayStartupRegistrationStore store, bool nativeFrontend)
        => new(Options(windows: true), nativeFrontend, SourceGui, Bootstrap, store);

    public void Dispose() => _directory.Delete(recursive: true);
}
