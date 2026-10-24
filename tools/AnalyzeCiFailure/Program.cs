// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace AnalyzeCiFailure;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args is not ["validate", .. var options] ||
            (options.Length != 0 && options is not ["--working-dir", _, "--scripts-dir", _]))
        {
            Console.Error.WriteLine("Usage: AnalyzeCiFailure validate [--working-dir <directory> --scripts-dir <directory>]");
            return 2;
        }

        var workingDirectory = options.Length == 0 ? Directory.GetCurrentDirectory() : Path.GetFullPath(options[1]);
        var scriptsDirectory = options.Length == 0 ? Path.Combine(workingDirectory, ".github", "workflows") : Path.GetFullPath(options[3]);
        var analysisDirectory = Environment.GetEnvironmentVariable("ANALYSIS_DIR");
        if (string.IsNullOrEmpty(analysisDirectory))
        {
            Console.WriteLine("::error::ANALYSIS_DIR is required (download-analysis step missing?)");
            return 1;
        }

        try
        {
            await new Validator(workingDirectory, scriptsDirectory, Path.GetFullPath(analysisDirectory, workingDirectory)).ValidateAsync();
            return 0;
        }
        catch (Exception ex) when (ex is ValidationException or IOException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception)
        {
            Console.WriteLine($"::error::{Json.Display(ex.Message)}");
            return 1;
        }
    }
}
