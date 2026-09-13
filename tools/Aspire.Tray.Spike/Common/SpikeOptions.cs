// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;

namespace Aspire.Tray.Spike;

internal sealed record SpikeOptions(string CliPath, int? SmokeSeconds, string? BundleRoot)
{
    public const string Usage = """
        aspire-tray start --cli <absolute Aspire executable path> --bundle-root <absolute bundle directory>
        aspire-tray stop
        aspire-tray --cli <absolute Aspire executable path> [--bundle-root <absolute bundle directory>] [--smoke-seconds <1-120>]
        """;

    public static SpikeOptions Parse(string[] args)
    {
        string? cli = null;
        int? smokeSeconds = null;
        string? bundleRoot = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--cli" && i + 1 < args.Length && cli is null)
            {
                cli = args[++i];
            }
            else if (args[i] == "--bundle-root" && i + 1 < args.Length && bundleRoot is null)
            {
                bundleRoot = args[++i];
            }
            else if (args[i] == "--smoke-seconds" && i + 1 < args.Length && smokeSeconds is null
                && int.TryParse(args[++i], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
                && seconds is >= 1 and <= 120)
            {
                smokeSeconds = seconds;
            }
            else
            {
                throw new ArgumentException(Usage);
            }
        }

        if (cli is null || !Path.IsPathFullyQualified(cli) || !File.Exists(cli))
        {
            throw new ArgumentException($"An existing absolute CLI path is required. {Usage}");
        }

        if (bundleRoot is not null && (!Path.IsPathFullyQualified(bundleRoot) || !Directory.Exists(bundleRoot)))
        {
            throw new ArgumentException("The bundle root must be an existing absolute directory.");
        }
        if (smokeSeconds is not null && bundleRoot is not null)
        {
            throw new ArgumentException("Native smoke must not use a live bundle lease.");
        }

        return new(cli, smokeSeconds, bundleRoot);
    }
}
