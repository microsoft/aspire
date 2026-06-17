// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Aspire.Cli.Resources;

namespace Aspire.Cli.Utils.EnvironmentChecker;

/// <summary>
/// Reports operating system information for the current environment.
/// </summary>
internal sealed class OperatingSystemCheck : IEnvironmentCheck
{
    internal const string CheckName = "operating-system";
    private const string UnknownOperatingSystemMetadataValue = "unknown";

    private readonly Func<OperatingSystemDetails> _getOperatingSystemDetails;

    public OperatingSystemCheck()
        : this(GetCurrentOperatingSystemDetails)
    {
    }

    internal OperatingSystemCheck(Func<OperatingSystemDetails> getOperatingSystemDetails)
    {
        ArgumentNullException.ThrowIfNull(getOperatingSystemDetails);

        _getOperatingSystemDetails = getOperatingSystemDetails;
    }

    public int Order => 10; // Fast local check, after Aspire version and before WSL-specific diagnostics.

    public Task<IReadOnlyList<EnvironmentCheckResult>> CheckAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var details = _getOperatingSystemDetails();
        var messageDisplayName = details.MessageDisplayName ?? details.Name;
        var result = new EnvironmentCheckResult
        {
            Category = EnvironmentCheckCategories.Environment,
            Name = CheckName,
            Status = details.Status,
            Message = string.Format(
                CultureInfo.CurrentCulture,
                DoctorCommandStrings.OperatingSystemMessageFormat,
                string.IsNullOrWhiteSpace(details.Version) ? messageDisplayName : $"{messageDisplayName} {details.Version}"),
            Metadata = BuildMetadata(details)
        };

        return Task.FromResult<IReadOnlyList<EnvironmentCheckResult>>([result]);
    }

    internal static OperatingSystemDetails GetCurrentOperatingSystemDetails()
    {
        var version = Environment.OSVersion.Version;
        var description = RuntimeInformation.OSDescription;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return CreateWindowsDetails(version, description);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return CreateMacOSDetails(version, description);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return CreateLinuxDetails(version, description);
        }

        return new OperatingSystemDetails(
            UnknownOperatingSystemMetadataValue,
            UnknownOperatingSystemMetadataValue,
            version.ToString(),
            description,
            EnvironmentCheckStatus.Warning,
            DoctorCommandStrings.VersionUnknown);
    }

    internal static OperatingSystemDetails CreateWindowsDetails(Version version, string description)
        => new("Windows", "Windows", version.ToString(), description, EnvironmentCheckStatus.Pass);

    internal static OperatingSystemDetails CreateMacOSDetails(Version version, string description)
        => new("macOS", "macOS", version.ToString(), description, EnvironmentCheckStatus.Pass);

    internal static OperatingSystemDetails CreateLinuxDetails(Version fallbackVersion, string description)
    {
        var osReleaseContents = TryReadLinuxOsRelease();
        var osRelease = ParseLinuxOsRelease(osReleaseContents);
        osRelease.TryGetValue("NAME", out var name);
        osRelease.TryGetValue("ID", out var id);
        osRelease.TryGetValue("VERSION_ID", out var version);
        osRelease.TryGetValue("PRETTY_NAME", out var prettyName);

        var distroName = NormalizeLinuxDistributionName(name, id);
        var displayName = string.IsNullOrWhiteSpace(distroName) ? "Linux" : $"Linux {distroName}";
        var linuxDescription = string.IsNullOrWhiteSpace(prettyName) ? description : prettyName;

        return new OperatingSystemDetails("Linux", displayName, string.IsNullOrWhiteSpace(version) ? fallbackVersion.ToString() : version, linuxDescription, EnvironmentCheckStatus.Pass);
    }

    internal static Dictionary<string, string> ParseLinuxOsRelease(string? contents)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(contents))
        {
            return values;
        }

        foreach (var rawLine in contents.Split('\n'))
        {
            // /etc/os-release uses KEY=VALUE lines, for example:
            //   NAME="Ubuntu"
            //   VERSION_ID="24.04"
            // Strip surrounding quotes for display. The values doctor surfaces are not expected
            // to contain shell-special characters, so leave any backslashes unchanged.
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = line[..separatorIndex];
            var value = line[(separatorIndex + 1)..].Trim();
            values[key] = UnquoteOsReleaseValue(value);
        }

        return values;
    }

    private static JsonObject BuildMetadata(OperatingSystemDetails details)
    {
        var metadata = new JsonObject
        {
            ["osType"] = details.Type,
            ["displayName"] = details.Name,
            ["version"] = details.Version
        };

        if (!string.IsNullOrWhiteSpace(details.Description))
        {
            metadata["description"] = details.Description;
        }

        return metadata;
    }

    private static string? TryReadLinuxOsRelease()
    {
        try
        {
            return File.ReadAllText("/etc/os-release");
        }
        catch
        {
            return null;
        }
    }

    private static string? NormalizeLinuxDistributionName(string? name, string? id)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return NormalizeLinuxDistributionId(id);
        }

        var normalizedName = name.Trim();
        foreach (var suffix in new[] { " GNU/Linux", " Linux" })
        {
            if (normalizedName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                normalizedName = normalizedName[..^suffix.Length];
                break;
            }
        }

        return normalizedName.Equals("Linux", StringComparison.OrdinalIgnoreCase) ? null : normalizedName;
    }

    private static string? NormalizeLinuxDistributionId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        return id.Trim().ToLowerInvariant() switch
        {
            "alpine" => "Alpine",
            "arch" => "Arch Linux",
            "centos" => "CentOS",
            "debian" => "Debian",
            "fedora" => "Fedora",
            "linuxmint" => "Linux Mint",
            "opensuse" or "opensuse-leap" or "opensuse-tumbleweed" => "openSUSE",
            "rhel" => "Red Hat Enterprise Linux",
            "ubuntu" => "Ubuntu",
            var value => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value.Replace('-', ' '))
        };
    }

    private static string UnquoteOsReleaseValue(string value)
    {
        if (value.Length < 2)
        {
            return value;
        }

        var quote = value[0];
        if ((quote != '"' && quote != '\'') || value[^1] != quote)
        {
            return value;
        }

        var unquoted = value[1..^1];
        return unquoted;
    }
}

internal sealed record OperatingSystemDetails(
    string Type,
    string Name,
    string Version,
    string Description,
    EnvironmentCheckStatus Status,
    string? MessageDisplayName = null);
