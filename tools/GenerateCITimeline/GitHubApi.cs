// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Text.Json;

internal static class GitHubApi
{
    public static async Task<JsonElement> CallAsync(string endpoint)
    {
        var psi = new ProcessStartInfo("gh", ["api", endpoint, "--paginate"])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start gh process.");
        // Read both streams concurrently to avoid deadlock when a pipe buffer fills.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        var stdout = stdoutTask.Result;
        var stderr = stderrTask.Result;
        await process.WaitForExitAsync().ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"gh api failed: {stderr.Trim()}");
        }

        var text = stdout.Trim();
        if (string.IsNullOrEmpty(text))
        {
            using var emptyDoc = JsonDocument.Parse("{}");
            return emptyDoc.RootElement.Clone();
        }

        // gh --paginate can produce multiple JSON objects concatenated together.
        // Parse them all and merge if needed.
        var objects = new List<JsonElement>();
        var reader = new Utf8JsonReader(
            System.Text.Encoding.UTF8.GetBytes(text),
#if NET9_0_OR_GREATER
            new JsonReaderOptions { AllowMultipleValues = true });
#else
            default);
#endif
        while (reader.Read())
        {
            if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
            {
                var element = JsonElement.ParseValue(ref reader);
                objects.Add(element);
            }
        }

        if (objects.Count == 1)
        {
            return objects[0];
        }

        // Merge arrays or objects with array values
        if (objects[0].ValueKind == JsonValueKind.Array)
        {
            var merged = new List<JsonElement>();
            foreach (var obj in objects)
            {
                if (obj.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in obj.EnumerateArray())
                    {
                        merged.Add(item);
                    }
                }
                else
                {
                    merged.Add(obj);
                }
            }

            using var doc = JsonDocument.Parse($"[{string.Join(",", merged.Select(e => e.GetRawText()))}]");
            return doc.RootElement.Clone();
        }

        if (objects[0].ValueKind == JsonValueKind.Object)
        {
            var merged = new Dictionary<string, List<string>>();
            foreach (var obj in objects)
            {
                foreach (var prop in obj.EnumerateObject())
                {
                    if (!merged.TryGetValue(prop.Name, out var list))
                    {
                        list = [];
                        merged[prop.Name] = list;
                    }

                    if (prop.Value.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in prop.Value.EnumerateArray())
                        {
                            list.Add(item.GetRawText());
                        }
                    }
                    else if (list.Count == 0)
                    {
                        list.Add(prop.Value.GetRawText());
                    }
                    else
                    {
                        // Overwrite scalar value
                        list.Clear();
                        list.Add(prop.Value.GetRawText());
                    }
                }
            }

            var parts = merged.Select(kv =>
            {
                var val = kv.Value.Count == 1 ? kv.Value[0] : $"[{string.Join(",", kv.Value)}]";
                return $"\"{kv.Key}\":{val}";
            });
            using var doc = JsonDocument.Parse($"{{{string.Join(",", parts)}}}");
            return doc.RootElement.Clone();
        }

        return objects[0];
    }

    public static async Task<(JsonElement RunInfo, List<JsonElement> Jobs)> FetchRunDataAsync(string repo, string runId)
    {
        var runInfo = await CallAsync($"/repos/{repo}/actions/runs/{runId}").ConfigureAwait(false);
        var jobsData = await CallAsync($"/repos/{repo}/actions/runs/{runId}/jobs").ConfigureAwait(false);

        var jobs = new List<JsonElement>();
        if (jobsData.ValueKind == JsonValueKind.Object && jobsData.TryGetProperty("jobs", out var jobsArray))
        {
            foreach (var j in jobsArray.EnumerateArray())
            {
                jobs.Add(j);
            }
        }
        else if (jobsData.ValueKind == JsonValueKind.Array)
        {
            foreach (var j in jobsData.EnumerateArray())
            {
                jobs.Add(j);
            }
        }

        return (runInfo, jobs);
    }

    public static (JsonElement RunInfo, List<JsonElement> Jobs) LoadJsonData(string path)
    {
        var text = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;

        JsonElement runInfo;
        if (root.TryGetProperty("run_info", out var ri))
        {
            runInfo = ri.Clone();
        }
        else
        {
            using var emptyDoc = JsonDocument.Parse("{}");
            runInfo = emptyDoc.RootElement.Clone();
        }

        var jobs = new List<JsonElement>();
        if (root.TryGetProperty("jobs", out var jobsArray))
        {
            foreach (var j in jobsArray.EnumerateArray())
            {
                jobs.Add(j.Clone());
            }
        }

        return (runInfo, jobs);
    }
}
