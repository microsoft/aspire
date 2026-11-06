// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;

namespace Aspire.Tray;

/// <summary>
/// Opt-in XDG autostart using the stable CLI entry point, never an extracted bundle.
/// </summary>
internal sealed class LinuxTrayStartupSettings(TrayOptions options, bool nativeFrontend, string directory)
    : RegisteredTrayStartupSettings(new FileTrayStartupRegistrationStore(Path.Combine(directory, FileName)))
{
    internal const string FileName = "dev.aspire.tray.desktop";
    private const string EntryPrefix = "X-Aspire-Tray-Entry=";

    internal static string GetAutostartDirectory()
    {
        var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        return Path.Combine(config is not null && Path.IsPathFullyQualified(config) ? config
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"), "autostart");
    }

    protected override string? UnavailableReason
        => TrayStartupEntry.GetUnavailableReason(options, nativeFrontend, windows: false, linux: true);

    protected override string CreateRegistration() => Serialize(options.StartupCliPath!);

    internal static string Serialize(string cli)
    {
        if (!Path.IsPathFullyQualified(cli) || cli.Any(char.IsControl))
        {
            throw new ArgumentException("Startup requires an absolute CLI path without control characters.");
        }
        // Desktop Exec is not a shell command. Quote argv, escape reserved characters,
        // then apply desktop string escaping; %% is a literal percent, not a field code.
        // For example /home/a b/$cli becomes "/home/a b/\\$cli".
        // https://specifications.freedesktop.org/desktop-entry-spec/latest/exec-variables.html
        var argument = cli.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("`", "\\`", StringComparison.Ordinal)
            .Replace("$", "\\$", StringComparison.Ordinal)
            .Replace("%", "%%", StringComparison.Ordinal)
            .Replace("\\", "\\\\", StringComparison.Ordinal);
        return $"""
            [Desktop Entry]
            Type=Application
            Name=Aspire Tray
            Comment=Manage local Aspire AppHosts
            Exec="{argument}" tray start --non-interactive --nologo
            Terminal=false
            X-Aspire-Tray-Version=1
            {EntryPrefix}{Convert.ToBase64String(Encoding.UTF8.GetBytes(cli))}

            """;
    }

    protected override bool IsOwned(string registration)
    {
        var entries = registration.Split('\n').Where(line => line.StartsWith(EntryPrefix, StringComparison.Ordinal)).ToArray();
        if (entries.Length != 1)
        {
            return false;
        }
        try
        {
            var path = Encoding.UTF8.GetString(Convert.FromBase64String(entries[0][EntryPrefix.Length..]));
            return string.Equals(registration, Serialize(path), StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            return false;
        }
    }
}
