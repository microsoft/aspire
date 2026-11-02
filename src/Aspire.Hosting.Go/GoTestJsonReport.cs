// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using System.Text.Json;
using Aspire.Hosting.ApplicationModel;

#pragma warning disable ASPIRETESTING001

namespace Aspire.Hosting.Go;

/// <summary>
/// Translates Go JSON events into top-level invocation outcomes without losing parent failures.
/// </summary>
internal static class GoTestJsonReport
{
    internal static async Task ReadAsync(Stream stream, Action<TestResult> publish, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), leaveOpen: true);
        var packages = new Dictionary<string, PackageState>(StringComparer.Ordinal);
        var buildFailures = new HashSet<string>(StringComparer.Ordinal);
        var sawPackageOutcome = false;
        var failedPackage = false;

        // go test -json emits interleaved newline-delimited events:
        // {"Action":"run","Package":"example/api","Test":"TestAPI/child"}
        // Go 1.24 also emits {"Action":"build-fail","ImportPath":"example/api"}.
        // Dispatch by Action, not Package, and retain invocation state until the matching terminal event.
        // https://pkg.go.dev/cmd/test2json#hdr-Output_Format
        // https://go.dev/doc/go1.24#go-command
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            using var document = JsonDocument.Parse(line);
            var entry = document.RootElement;
            var action = RequiredString(entry, "Action");
            if (action is "build-output" or "build-fail")
            {
                var importPath = RequiredString(entry, "ImportPath");
                if (action == "build-fail")
                {
                    buildFailures.Add(importPath);
                }
                else
                {
                    RequiredString(entry, "Output", allowEmpty: true);
                }
                continue;
            }
            if (action is not ("start" or "run" or "pause" or "cont" or "output" or "pass" or "fail" or "skip"))
            {
                throw new InvalidDataException($"Unsupported Go test action '{action}'. Benchmarks are not supported.");
            }
            var packageName = RequiredString(entry, "Package");
            if (!packages.TryGetValue(packageName, out var package))
            {
                package = new PackageState();
                packages.Add(packageName, package);
            }
            if (package.Completed)
            {
                throw new InvalidDataException($"Go event after package completion: '{packageName}'.");
            }
            var testName = OptionalString(entry, "Test");
            if (testName is not null && string.IsNullOrWhiteSpace(testName))
            {
                throw new InvalidDataException("Go test names must not be empty.");
            }
            if (testName is null)
            {
                if (action == "start")
                {
                    if (package.Started)
                    {
                        throw new InvalidDataException($"Repeated package start for '{packageName}'.");
                    }
                    package.Started = true;
                }
                else if (action is "pass" or "fail" or "skip")
                {
                    if (package.Completed || package.Active.Count != 0)
                    {
                        throw new InvalidDataException($"Incomplete or repeated package outcome for '{packageName}'.");
                    }
                    package.Completed = true;
                    sawPackageOutcome = true;
                    failedPackage |= action == "fail";
                }
                else if (action == "output")
                {
                    RequiredString(entry, "Output", allowEmpty: true);
                }
                else
                {
                    throw new InvalidDataException($"Go action '{action}' requires a test name.");
                }
                continue;
            }
            if (action == "run")
            {
                var ordinal = package.Invocations.GetValueOrDefault(testName) + 1;
                package.Invocations[testName] = ordinal;
                if (!package.Active.TryAdd(testName, new Invocation(ordinal)))
                {
                    throw new InvalidDataException($"Overlapping Go test invocation '{testName}'.");
                }
                continue;
            }
            if (!package.Active.TryGetValue(testName, out var invocation))
            {
                throw new InvalidDataException($"Go test '{testName}' has no active invocation.");
            }
            if (action == "output")
            {
                invocation.Append(RequiredString(entry, "Output", allowEmpty: true));
            }
            else if (action is "pass" or "fail" or "skip")
            {
                var outcome = action switch
                {
                    "pass" => TestOutcome.Passed,
                    "fail" => TestOutcome.Failed,
                    _ => TestOutcome.Skipped
                };
                var duration = ReadDuration(entry);
                if (package.Active.Keys.Any(name => name.StartsWith(testName + "/", StringComparison.Ordinal)))
                {
                    throw new InvalidDataException($"Go test '{testName}' completed before its subtests.");
                }
                package.Active.Remove(testName);
                var separator = testName.IndexOf('/');
                if (separator >= 0)
                {
                    var parentName = testName[..separator];
                    if (!package.Active.TryGetValue(parentName, out var parent))
                    {
                        throw new InvalidDataException($"Go subtest '{testName}' has no active top-level parent.");
                    }
                    parent.Append($"{testName} [invocation {invocation.Ordinal}]: {outcome}\n{invocation.Message}");
                    parent.ChildFailed |= outcome == TestOutcome.Failed;
                }
                else
                {
                    if (invocation.ChildFailed && outcome != TestOutcome.Failed)
                    {
                        throw new InvalidDataException($"Go parent '{testName}' passed despite a failed subtest.");
                    }
                    publish(new TestResult
                    {
                        Name = $"{packageName}.{testName} [invocation {invocation.Ordinal}]",
                        Outcome = outcome,
                        Duration = duration,
                        Message = invocation.Message.Length == 0 ? null : invocation.Message
                    });
                }
            }
            else if (action is not ("pause" or "cont"))
            {
                throw new InvalidDataException($"Unexpected Go test action '{action}'.");
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!sawPackageOutcome || packages.Values.Any(p => !p.Completed || p.Active.Count != 0))
        {
            throw new InvalidDataException("Go test output is empty or incomplete; no complete package outcome was available.");
        }
        if (failedPackage || buildFailures.Count != 0)
        {
            // Package/TestMain/build failures are not fabricated test cases. Throw after harvesting
            // named outcomes so even an anomalous zero process exit cannot turn a failed package green.
            throw new InvalidDataException("Go reported a package or build failure. Inspect the raw stdout and stderr artifacts.");
        }
    }

    private static string RequiredString(JsonElement entry, string name, bool allowEmpty = false)
    {
        var value = OptionalString(entry, name);
        if (value is null || !allowEmpty && string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException($"Missing Go event property '{name}'.");
        }

        return value;
    }

    private static string? OptionalString(JsonElement entry, string name)
    {
        return entry.TryGetProperty(name, out var value) ? value.GetString() : null;
    }

    private static TimeSpan? ReadDuration(JsonElement entry)
    {
        if (!entry.TryGetProperty("Elapsed", out var elapsed))
        {
            return null;
        }
        var seconds = elapsed.GetDouble();
        if (!double.IsFinite(seconds) || seconds < 0 || seconds >= TimeSpan.MaxValue.TotalSeconds)
        {
            throw new InvalidDataException("Invalid Go test duration.");
        }

        return TimeSpan.FromSeconds(seconds);
    }

    private sealed class PackageState
    {
        public bool Started { get; set; }
        public bool Completed { get; set; }
        public Dictionary<string, Invocation> Active { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> Invocations { get; } = new(StringComparer.Ordinal);
    }

    private sealed class Invocation(int ordinal)
    {
        private const int MaximumMessageLength = 64 * 1024;
        private readonly StringBuilder _message = new();
        private bool _truncated;
        public int Ordinal { get; } = ordinal;
        public bool ChildFailed { get; set; }
        public string Message => _message.ToString() + (_truncated ? "\n[Message truncated; complete output is in the raw artifact.]" : "");

        public void Append(string value)
        {
            var remaining = MaximumMessageLength - _message.Length;
            _message.Append(value.AsSpan(0, Math.Min(remaining, value.Length)));
            _truncated |= value.Length > remaining;
        }
    }
}
