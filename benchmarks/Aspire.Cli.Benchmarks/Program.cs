// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using BenchmarkDotNet.Running;

namespace Aspire.Cli.Benchmarks;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        string[] passthrough;
        RunOptions options;

        try
        {
            (passthrough, options) = ParseArgs(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        try
        {
            await CorpusLoader.EnsureCorpusAsync(options).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to load corpus: {ex.Message}");
            return 1;
        }

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(passthrough);
        return 0;
    }

    private static (string[] Passthrough, RunOptions Options) ParseArgs(string[] args)
    {
        var refresh = false;
        string? input = null;
        var remaining = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--refresh":
                    refresh = true;
                    break;

                case "--input":
                    if (i + 1 >= args.Length)
                    {
                        throw new ArgumentException("--input requires a path argument.");
                    }

                    if (args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        throw new ArgumentException($"--input requires a path argument, but got switch '{args[i + 1]}'.");
                    }

                    input = args[++i];
                    break;

                default:
                    remaining.Add(args[i]);
                    break;
            }
        }

        return (remaining.ToArray(), new RunOptions(input, refresh));
    }
}

internal sealed record RunOptions(string? InputPath, bool Refresh);
