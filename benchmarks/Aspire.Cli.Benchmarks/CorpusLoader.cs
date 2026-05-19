// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Benchmarks;

internal static class CorpusLoader
{
    private const string DefaultUrl = "https://aspire.dev/llms-full.txt";
    private const string DefaultFileName = "llms-full.txt";

    public static string ResolvePath(RunOptions options)
    {
        if (!string.IsNullOrEmpty(options.InputPath))
        {
            return options.InputPath;
        }

        var envPath = Environment.GetEnvironmentVariable("LLMS_FULL_TXT");
        if (!string.IsNullOrEmpty(envPath))
        {
            return envPath;
        }

        // No persistent cache: create a fresh, securely-randomized temp subdirectory
        // per process so every benchmark run gets an isolated download location. The
        // directory is registered for best-effort cleanup when the harness exits.
        var tempDir = Directory.CreateTempSubdirectory("aspire-bench-");
        AppDomain.CurrentDomain.ProcessExit += (_, _) => DeleteTempDirectory(tempDir);
        return Path.Combine(tempDir.FullName, DefaultFileName);
    }

    public static async Task<string> EnsureCorpusAsync(RunOptions options, CancellationToken cancellationToken = default)
    {
        var path = ResolvePath(options);

        if (!options.Refresh && File.Exists(path))
        {
            return path;
        }

        Console.Error.WriteLine($"Downloading {DefaultUrl} -> {path} ...");
        using var http = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(2),
        };

        var response = await http.GetAsync(DefaultUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var output = File.Create(path))
        {
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        }

        var size = new FileInfo(path).Length;
        Console.Error.WriteLine($"Saved {size:N0} bytes to {path}");
        return path;
    }

    private static void DeleteTempDirectory(DirectoryInfo directory)
    {
        try
        {
            directory.Delete(recursive: true);
        }
        // Best-effort cleanup only: benchmark traces, antivirus, or a crashed child
        // process can still hold files open at process exit. In that case the securely
        // created temp directory can be left for the OS/user temp cleanup policy.
        catch (DirectoryNotFoundException)
        {
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
