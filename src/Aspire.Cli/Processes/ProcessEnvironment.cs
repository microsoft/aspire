// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace Aspire.Cli.Processes;

internal static class ProcessEnvironment
{
    // OrdinalIgnoreCase mirrors ProcessStartInfo's behavior on Windows (env vars are
    // case-insensitive). Using it on all platforms is slightly less strict than the
    // Unix kernel (which treats env names as bytes) but it matches what ProcessStartInfo
    // does and prevents the trap of accidentally having both "Path" and "PATH" entries.
    public static StringComparer Comparer => StringComparer.OrdinalIgnoreCase;

    public static Dictionary<string, string?> LoadParentEnvironment()
    {
        var parent = Environment.GetEnvironmentVariables();
        var dict = new Dictionary<string, string?>(parent.Count, Comparer);
        foreach (System.Collections.DictionaryEntry entry in parent)
        {
            dict[(string)entry.Key] = entry.Value as string;
        }

        return dict;
    }

    public static void ApplyTo(ProcessStartInfo startInfo, IReadOnlyDictionary<string, string?>? environment)
    {
        if (environment is null)
        {
            return;
        }

        startInfo.Environment.Clear();
        foreach (var (key, value) in environment)
        {
            // Match ProcessStartInfo.Environment semantics: a null value means "do not
            // set this variable in the child"; we get there by simply not adding it.
            if (value is not null)
            {
                startInfo.Environment[key] = value;
            }
        }
    }
}
