// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Aspire.Hosting.ApplicationModel;

#pragma warning disable ASPIRETESTING001 // Translates MSTest reports into the experimental testing contract.

namespace Aspire.Hosting.Dotnet;

/// <summary>
/// Reads a single, finalized MTP TRX report without traversing attachment paths or merged reports.
/// </summary>
internal static class MsTestTrxReport
{
    internal const string FileName = "results.trx";
    private static readonly XNamespace s_namespace = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    internal static async Task CollectAsync(ResourceTestingContext context)
    {
        // A nonzero exit can still produce a complete report containing useful failed results.
        await context.WaitForCompletionAsync().ConfigureAwait(false);
        for (var directory = new DirectoryInfo(context.OutputPath); directory is not null; directory = directory.Parent)
        {
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("The MSTest report directory must not contain symbolic links or reparse points.");
            }
            if (directory.FullName == context.RunContext.OutputPath)
            {
                break;
            }
        }
        var path = Path.Combine(context.OutputPath, FileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                "MSTest did not produce results.trx. The project must be an MTP-enabled executable with " +
                "Microsoft.Testing.Extensions.TrxReport registered (included in MSTest.Sdk's Default profile). " +
                "Inspect the resource logs for build, runner, or report errors.", path);
        }
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("The MSTest report must not be a symbolic link or reparse point.");
        }
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var _ = stream.ConfigureAwait(false);
        var report = await ReadAsync(stream, context.CancellationToken).ConfigureAwait(false);
        foreach (var result in report.Results)
        {
            context.PublishResult(result);
        }
        if (report.Failed && report.Results.All(result => result.Outcome != TestOutcome.Failed))
        {
            throw new InvalidDataException("The TRX run failed without an individual failed test. Inspect the raw report and resource logs.");
        }
    }

    internal static async Task<Report> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            Async = true,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 64 * 1024 * 1024
        });
        var document = await XDocument.LoadAsync(reader, LoadOptions.None, cancellationToken).ConfigureAwait(false);
        if (document.Root is not { } root || root.Name != s_namespace + "TestRun")
        {
            throw new InvalidDataException("The MSTest report must contain a TRX TestRun in the TeamTest/2010 namespace.");
        }
        ReadId(root, "id");
        var definitions = new Dictionary<Guid, XElement>();
        foreach (var definition in RequiredElement(root, "TestDefinitions").Elements(s_namespace + "UnitTest"))
        {
            if (!definitions.TryAdd(ReadId(definition, "id"), definition))
            {
                throw new InvalidDataException("The TRX report contains duplicate test definitions.");
            }
        }
        var results = new List<TestResult>();
        var executionIds = new HashSet<Guid>();
        // MTP emits flat Results/UnitTestResult entries:
        // <UnitTestResult executionId="..." testId="..." testName="case(1)"
        //   outcome="NotExecuted" duration="00:00:00.0000000"><Output>...</Output></UnitTestResult>
        // Data rows may share a testId/display name but have distinct executionIds. Do not
        // group by name, or scan merged/*.trx alongside this canonical single-process report.
        // https://learn.microsoft.com/dotnet/core/testing/microsoft-testing-platform-test-reports
        foreach (var entry in RequiredElement(root, "Results").Elements())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Name != s_namespace + "UnitTestResult" || entry.Element(s_namespace + "InnerResults") is not null)
            {
                throw new InvalidDataException("Only flat MTP UnitTestResult entries are supported.");
            }
            if (!executionIds.Add(ReadId(entry, "executionId")))
            {
                throw new InvalidDataException("The TRX report contains duplicate execution identities.");
            }
            var testId = ReadId(entry, "testId");
            if (!definitions.TryGetValue(testId, out var definition))
            {
                throw new InvalidDataException("A TRX result references a missing test definition.");
            }
            var name = RequiredAttribute(entry, "testName");
            var className = definition.Element(s_namespace + "TestMethod")?.Attribute("className")?.Value;
            if (!string.IsNullOrWhiteSpace(className) && !name.StartsWith(className + ".", StringComparison.Ordinal))
            {
                name = className + "." + name;
            }
            var outcome = RequiredAttribute(entry, "outcome") switch
            {
                "Passed" => TestOutcome.Passed,
                "Failed" => TestOutcome.Failed,
                "NotExecuted" => TestOutcome.Skipped,
                var unknown => throw new InvalidDataException($"Unsupported MSTest TRX outcome '{unknown}'.")
            };
            TimeSpan? duration = null;
            if (entry.Attribute("duration") is { } durationAttribute)
            {
                if (!TimeSpan.TryParseExact(durationAttribute.Value, "c", CultureInfo.InvariantCulture, out var parsed) ||
                    parsed < TimeSpan.Zero)
                {
                    throw new InvalidDataException($"Invalid TRX duration '{durationAttribute.Value}'.");
                }
                duration = parsed;
            }
            var output = entry.Element(s_namespace + "Output");
            var error = output?.Element(s_namespace + "ErrorInfo");
            var messages = new[] { error?.Element(s_namespace + "Message")?.Value,
                error?.Element(s_namespace + "StackTrace")?.Value,
                output?.Element(s_namespace + "StdOut")?.Value,
                output?.Element(s_namespace + "StdErr")?.Value,
                output?.Element(s_namespace + "DebugTrace")?.Value }
                .Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
            results.Add(new TestResult
            {
                Name = name,
                Outcome = outcome,
                Duration = duration,
                Message = messages.Length == 0 ? null : string.Join(Environment.NewLine, messages)
            });
        }
        var failed = ValidateSummary(root, results);

        return new Report(results, failed);
    }

    private static bool ValidateSummary(XElement root, List<TestResult> results)
    {
        var summary = RequiredElement(root, "ResultSummary");
        var outcome = RequiredAttribute(summary, "outcome");
        if (outcome is not ("Completed" or "Passed" or "Failed"))
        {
            throw new InvalidDataException($"Unsupported TRX run outcome '{outcome}'.");
        }
        var counters = RequiredElement(summary, "Counters");
        var failed = ReadCount(counters, "failed");
        var timedOut = ReadCount(counters, "timeout");
        if (ReadCount(counters, "total") != results.Count ||
            ReadCount(counters, "passed") != results.Count(result => result.Outcome == TestOutcome.Passed) ||
            ReadCount(counters, "notExecuted") != results.Count(result => result.Outcome == TestOutcome.Skipped) ||
            (long)failed + timedOut != results.Count(result => result.Outcome == TestOutcome.Failed))
        {
            throw new InvalidDataException("The TRX summary counters do not match the collected test results.");
        }
        foreach (var counter in new[] { "error", "aborted", "inconclusive", "notRunnable", "disconnected", "warning", "inProgress", "pending" })
        {
            if (counters.Attribute(counter) is not null && ReadCount(counters, counter) != 0)
            {
                throw new InvalidDataException($"Unsupported nonzero TRX counter '{counter}'.");
            }
        }

        return outcome == "Failed";
    }

    private static int ReadCount(XElement element, string name)
    {
        if (!int.TryParse(RequiredAttribute(element, name), NumberStyles.None, CultureInfo.InvariantCulture, out var value))
        {
            throw new InvalidDataException($"Invalid TRX counter '{name}'.");
        }

        return value;
    }

    private static Guid ReadId(XElement element, string name)
    {
        if (!Guid.TryParse(RequiredAttribute(element, name), out var id) || id == Guid.Empty)
        {
            throw new InvalidDataException($"Invalid TRX identity '{name}'.");
        }

        return id;
    }

    private static string RequiredAttribute(XElement element, string name)
    {
        var value = element.Attribute(name)?.Value;
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException($"Missing TRX attribute '{name}' on '{element.Name.LocalName}'.");
        }

        return value;
    }

    private static XElement RequiredElement(XElement element, string name)
    {
        var elements = element.Elements(s_namespace + name).Take(2).ToArray();
        if (elements.Length != 1)
        {
            throw new InvalidDataException($"The TRX report must contain exactly one '{name}' element.");
        }

        return elements[0];
    }

    internal sealed record Report(IReadOnlyList<TestResult> Results, bool Failed);
}
