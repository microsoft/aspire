using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

/// <summary>An error to show the user, without a stack trace.</summary>
sealed class VideoException(string message) : Exception(message);

/// <summary>
/// The external tools: ffmpeg (encoding) and Node.js (the voice, through tts/tts.mjs). Both are installed by the
/// user, at the versions of references/specs.md; FFMPEG and NODE name other executables than those on the PATH.
/// </summary>
static partial class Media
{
    public const int MinimumFfmpegMajor = 7;
    public const int MinimumNodeMajor = 22;

    public static string Ffmpeg { get; } = Environment.GetEnvironmentVariable("FFMPEG") is { Length: > 0 } ffmpeg ? ffmpeg : "ffmpeg";

    public static string Node { get; } = Environment.GetEnvironmentVariable("NODE") is { Length: > 0 } node ? node : "node";

    public static (int ExitCode, string Output, string Error) Run(string file, IEnumerable<string> arguments, string? input = null)
    {
        var info = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = input is not null,
            // tts.mjs reads and writes UTF-8, and ffmpeg prints UTF-8 paths: the console's code page (on Windows, often
            // not UTF-8) would corrupt non-ASCII narration, such as Spanish. BOM-free, so stdin doesn't start with one.
            StandardInputEncoding = input is not null ? new System.Text.UTF8Encoding(false) : null,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        Process process;
        try
        {
            process = Process.Start(info)!;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new VideoException($"Can't run \"{file}\": install it, or set {(file == Ffmpeg ? "FFMPEG" : "NODE")} to its path. See the prerequisites in SKILL.md.");
        }

        using (process)
        {
            if (input is not null)
            {
                process.StandardInput.Write(input);
                process.StandardInput.Close();
            }

            // Reads both streams at once: a full stderr pipe would block the process while stdout is read.
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            return (process.ExitCode, output.Result, error.Result);
        }
    }

    /// <summary>Runs ffmpeg quietly, overwriting its outputs, and stops with its error when it fails.</summary>
    public static void FfmpegRun(IEnumerable<string> arguments)
    {
        var (exitCode, _, error) = Run(Ffmpeg, ["-hide_banner", "-loglevel", "error", "-y", .. arguments]);
        if (exitCode != 0)
        {
            throw new VideoException($"ffmpeg failed:\n{error.Trim()}");
        }
    }

    /// <summary>Checks that ffmpeg is installed, recent enough, and has the H.264 encoder (libx264).</summary>
    public static void CheckFfmpeg()
    {
        var (_, version, _) = Run(Ffmpeg, ["-hide_banner", "-version"]);
        // The first line looks like "ffmpeg version 7.1 Copyright ...", "ffmpeg version n7.1.1-..." or
        // "ffmpeg version 7.1.1-full_build-www.gyan.dev ...". A git build ("N-118123-g...") has no release number.
        var match = VersionRegex().Match(version);
        if (match.Success && int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) < MinimumFfmpegMajor)
        {
            throw new VideoException($"ffmpeg {match.Groups[1].Value} is too old: install ffmpeg {MinimumFfmpegMajor} or later.");
        }

        var (_, encoders, _) = Run(Ffmpeg, ["-hide_banner", "-encoders"]);
        if (!encoders.Contains("libx264", StringComparison.Ordinal))
        {
            throw new VideoException("This ffmpeg has no libx264 (H.264) encoder: install a full build of ffmpeg.");
        }
    }

    /// <summary>Checks that Node.js is installed, at a supported long-term support version or later.</summary>
    public static void CheckNode()
    {
        var (_, version, _) = Run(Node, ["--version"]);
        // "v24.22.1"
        if (!int.TryParse(version.TrimStart('v').Split('.')[0], CultureInfo.InvariantCulture, out var major) || major < MinimumNodeMajor)
        {
            throw new VideoException($"Node.js {version.Trim()} is too old: install Node.js {MinimumNodeMajor} or later (24 LTS recommended).");
        }

        if (!File.Exists(Path.Combine(Brand.SkillTools, "tts", "node_modules", "msedge-tts", "package.json")))
        {
            throw new VideoException($"The voice package isn't installed: run \"npm ci --prefix {Path.Combine(Brand.SkillTools, "tts")}\".");
        }
    }

    /// <summary>The length of a media file, in seconds.</summary>
    public static double Duration(string path)
    {
        // ffmpeg prints the input's properties on stderr, then fails for lack of an output, such as:
        //   Input #0, mp3, from 'intro.mp3':
        //     Duration: 00:00:04.37, start: 0.025057, bitrate: 48 kb/s
        var (_, _, error) = Run(Ffmpeg, ["-hide_banner", "-i", path]);
        var match = DurationRegex().Match(error);
        if (!match.Success)
        {
            throw new VideoException($"Can't read the length of {path}:\n{error.Trim()}");
        }

        return int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * 3600
            + int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) * 60
            + double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// A "file" line of an ffmpeg concat list, quoted by the concat format's rules (a quote is closed, escaped and
    /// reopened), so a path with an apostrophe, such as C:/Users/O'Neil, still works. "-safe 0" doesn't escape
    /// anything. See https://ffmpeg.org/ffmpeg-formats.html#concat-1.
    /// </summary>
    public static string ConcatEntry(string path) => "file '" + path.Replace('\\', '/').Replace("'", "'\\''") + "'";

    public static string Seconds(double value) => value.ToString("0.000", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"version n?(\d+)\.")]
    private static partial Regex VersionRegex();

    [GeneratedRegex(@"Duration: (\d+):(\d+):([\d.]+)")]
    private static partial Regex DurationRegex();
}
