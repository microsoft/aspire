// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Validation and path-normalization helpers for JavaScript hosting APIs.
// These are kept in one place so the public extension class doesn't need to
// describe the validation rules inline at every call site.

namespace Aspire.Hosting.JavaScript.Internal;

internal static class JavaScriptResourceValidation
{
    private static readonly string[] s_nextConfigFileNames = ["next.config.ts", "next.config.js", "next.config.mjs"];

    /// <summary>
    /// Recognized Next.js config file names, in lookup order.
    /// </summary>
    public static IReadOnlyList<string> NextConfigFileNames => s_nextConfigFileNames;

    /// <summary>
    /// Verifies that the path is URL-safe (alphanumerics plus <c>/</c>, <c>-</c>, <c>_</c>).
    /// Used by the Yarp-fronted static-website publish path so the API mount
    /// point cannot inject query strings or path-traversal segments.
    /// </summary>
    public static void ValidateApiPath(string apiPath)
    {
        foreach (var c in apiPath)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not '/' and not '-' and not '_')
            {
                throw new ArgumentException($"The apiPath must contain only URL-safe path characters (alphanumeric, '/', '-', '_'). Invalid character: '{c}'", nameof(apiPath));
            }
        }
    }

    /// <summary>
    /// Normalizes a relative virtual container path to use forward slashes,
    /// strips leading <c>./</c>, and rejects absolute paths or <c>..</c>
    /// segments. Operates on container-virtual paths, so platform-specific
    /// resolution (e.g. <see cref="Path.GetFullPath(string)"/>) is intentionally
    /// avoided.
    /// </summary>
    public static string NormalizeRelativePath(string path)
    {
        var normalizedPath = path.Replace('\\', '/');

        if (normalizedPath.StartsWith("./", StringComparison.Ordinal))
        {
            normalizedPath = normalizedPath[2..];
        }

        if (normalizedPath.StartsWith('/'))
        {
            throw new ArgumentException("The path must be a relative path.", nameof(path));
        }

        // Reject path traversal segments. These are virtual Docker container paths (not host
        // filesystem paths), so Path.GetFullPath cannot be used — it produces platform-specific
        // results (e.g. D:\app\dist on Windows). Segment-based validation works correctly
        // cross-platform for container paths.
        var segments = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            if (segment == "..")
            {
                throw new ArgumentException("The path must not contain \"..\" segments.", nameof(path));
            }
        }

        return string.Join('/', segments);
    }

    /// <summary>
    /// Validates that a Next.js config file in <paramref name="appDirectory"/>
    /// declares <c>output: "standalone"</c>. Required by AddNextJsApp so the
    /// generated Dockerfile can copy the standalone-mode build artifacts.
    /// </summary>
    public static void ValidateNextJsStandaloneOutput(string appDirectory)
    {
        foreach (var configFileName in s_nextConfigFileNames)
        {
            var configPath = Path.Combine(appDirectory, configFileName);
            if (!File.Exists(configPath))
            {
                continue;
            }

            try
            {
                var content = File.ReadAllText(configPath);

                // Check for quoted "standalone" (double or single quotes) to reduce false positives
                if (!content.Contains("\"standalone\"") && !content.Contains("'standalone'"))
                {
                    throw new InvalidOperationException(
                        $"The Next.js config file '{configFileName}' does not contain 'output: \"standalone\"'. " +
                        "AddNextJsApp requires Next.js standalone output mode to generate a working Dockerfile. " +
                        "Add 'output: \"standalone\"' to the nextConfig object in your Next.js config file.");
                }
            }
            catch (IOException)
            {
                // If we can't read the config, skip the check — the Docker build will surface the error.
            }

            return;
        }

        throw new InvalidOperationException(
            "No Next.js configuration file found. AddNextJsApp expects one of: " +
            string.Join(", ", s_nextConfigFileNames));
    }
}
