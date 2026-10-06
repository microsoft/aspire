// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Xml;
using System.Xml.Linq;

namespace Aspire.Tray;

/// <summary>
/// Registers a one-shot next-login LaunchAgent in an explicitly selected user directory.
/// </summary>
internal sealed class MacTrayStartupSettings(TrayOptions options, bool nativeFrontend, string launchAgentsDirectory)
    : RegisteredTrayStartupSettings(new FileTrayStartupRegistrationStore(Path.Combine(launchAgentsDirectory, FileName)))
{
    internal const string Label = "dev.aspire.tray.login";
    internal const string FileName = Label + ".plist";

    protected override string? UnavailableReason => TrayStartupEntry.GetUnavailableReason(options, nativeFrontend, windows: false);

    protected override string CreateRegistration() => Serialize(options.StartupCliPath!);

    internal static string Serialize(string cliPath)
    {
        // RunAtLoad executes once when the next login loads this agent. Deliberately omit
        // KeepAlive and do not invoke launchctl: quitting the tray must remain a quit.
        // https://www.manpagez.com/man/5/launchd.plist/
        var document = new XDocument(new XDeclaration("1.0", "utf-8", null),
            new XDocumentType("plist", "-//Apple//DTD PLIST 1.0//EN", "http://www.apple.com/DTDs/PropertyList-1.0.dtd", null),
            new XElement("plist", new XAttribute("version", "1.0"),
                new XElement("dict",
                    new XElement("key", "Label"), new XElement("string", Label),
                    new XElement("key", "ProgramArguments"),
                    new XElement("array", new[] { cliPath, "tray", "start", "--non-interactive", "--nologo" }
                        .Select(argument => new XElement("string", argument))),
                    new XElement("key", "RunAtLoad"), new XElement("true"))));
        return document.ToString();
    }

    protected override bool IsOwned(string registration)
    {
        try
        {
            var document = Parse(registration);
            var cli = document.Root?.Element("dict")?.Element("array")?.Elements("string").FirstOrDefault()?.Value;
            return cli is not null && Path.IsPathFullyQualified(cli)
                && XNode.DeepEquals(document.Root, Parse(Serialize(cli)).Root);
        }
        catch (XmlException)
        {
            return false;
        }
    }

    private static XDocument Parse(string xml)
    {
        using var input = new StringReader(xml);
        using var reader = XmlReader.Create(input, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            MaxCharactersInDocument = 64 * 1024
        });
        return XDocument.Load(reader);
    }
}
