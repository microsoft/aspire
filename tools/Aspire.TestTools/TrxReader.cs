// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Xml.Serialization;

namespace Aspire.TestTools;

public class TrxReader
{
    public static IList<TestResult> GetTestResultsFromTrx(string filepath, Func<string, string, bool>? testFilter = null)
    {
        XmlSerializer serializer = new(typeof(TestRun));
        using FileStream fileStream = new(filepath, FileMode.Open);

        if (serializer.Deserialize(fileStream) is not TestRun testRun || testRun.Results?.UnitTestResults is null)
        {
            return Array.Empty<TestResult>();
        }

        var testResults = new List<TestResult>();

        foreach (var unitTestResult in testRun.Results.UnitTestResults)
        {
            if (string.IsNullOrEmpty(unitTestResult.TestName) || string.IsNullOrEmpty(unitTestResult.Outcome))
            {
                continue;
            }

            if (testFilter is not null && !testFilter(unitTestResult.TestName, unitTestResult.Outcome))
            {
                continue;
            }

            var startTime = unitTestResult.StartTime;
            var endTime = unitTestResult.EndTime;

            testResults.Add(new TestResult(
                Name: unitTestResult.TestName,
                Outcome: unitTestResult.Outcome,
                StartTime: startTime is null ? TimeSpan.MinValue : TimeSpan.Parse(startTime, CultureInfo.InvariantCulture),
                EndTime: endTime is null ? TimeSpan.MinValue : TimeSpan.Parse(endTime, CultureInfo.InvariantCulture),
                ErrorMessage: unitTestResult.Output?.ErrorInfoString,
                Stdout: unitTestResult.Output?.StdOut
            ));
        }

        return testResults;
    }

    public static IList<DetailedTestResult> GetDetailedTestResultsFromTrx(string filepath, Func<DetailedTestResult, bool>? testFilter = null)
    {
        var testRun = DeserializeTrxFile(filepath);
        if (testRun?.Results?.UnitTestResults is null)
        {
            return Array.Empty<DetailedTestResult>();
        }

        var testDefinitions = testRun.TestDefinitions?.UnitTests?
            .Where(static testDefinition => !string.IsNullOrWhiteSpace(testDefinition.Id))
            .ToDictionary(static testDefinition => testDefinition.Id!, StringComparer.OrdinalIgnoreCase);

        var testResults = new List<DetailedTestResult>();

        foreach (var unitTestResult in testRun.Results.UnitTestResults)
        {
            if (string.IsNullOrWhiteSpace(unitTestResult.TestName) || string.IsNullOrWhiteSpace(unitTestResult.Outcome))
            {
                continue;
            }

            var canonicalName = GetCanonicalTestName(unitTestResult, testDefinitions);
            var detailedTestResult = new DetailedTestResult(
                CanonicalName: canonicalName,
                DisplayName: unitTestResult.TestName,
                Outcome: unitTestResult.Outcome,
                ErrorMessage: unitTestResult.Output?.ErrorMessage,
                StackTrace: unitTestResult.Output?.StackTrace,
                Stdout: unitTestResult.Output?.StdOut);

            if (testFilter is not null && !testFilter(detailedTestResult))
            {
                continue;
            }

            testResults.Add(detailedTestResult);
        }

        return testResults;
    }

    public static TestRun? DeserializeTrxFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException($"{nameof(filePath)} cannot be null or empty.", nameof(filePath));
        }

        XmlSerializer serializer = new(typeof(TestRun));

        using FileStream fileStream = new(filePath, FileMode.Open, FileAccess.Read);
        return serializer.Deserialize(fileStream) as TestRun;
    }

    public static double GetTestRunDurationInMinutes(TestRun testRun)
    {
        // First try to use the Times element if available
        if (testRun?.Times is not null 
            && !string.IsNullOrEmpty(testRun.Times.Start) 
            && !string.IsNullOrEmpty(testRun.Times.Finish))
        {
            if (DateTime.TryParse(testRun.Times.Start, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var start)
                && DateTime.TryParse(testRun.Times.Finish, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var finish))
            {
                return (finish - start).TotalMinutes;
            }
        }

        // Fall back to computing from individual test results if Times element is not available
        if (testRun?.Results?.UnitTestResults is null || testRun.Results.UnitTestResults.Count == 0)
        {
            return 0.0;
        }

        DateTime? earliestStartTime = null;
        DateTime? latestEndTime = null;

        foreach (var unitTestResult in testRun.Results.UnitTestResults)
        {
            if (DateTime.TryParse(unitTestResult.StartTime, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var startTime))
            {
                if (earliestStartTime is null || startTime < earliestStartTime)
                {
                    earliestStartTime = startTime;
                }
            }

            if (DateTime.TryParse(unitTestResult.EndTime, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var endTime))
            {
                if (latestEndTime is null || endTime > latestEndTime)
                {
                    latestEndTime = endTime;
                }
            }
        }

        if (earliestStartTime is null || latestEndTime is null)
        {
            return 0.0;
        }

        return (latestEndTime.Value - earliestStartTime.Value).TotalMinutes;
    }

    private static string GetCanonicalTestName(UnitTestResult unitTestResult, IReadOnlyDictionary<string, UnitTestDefinition>? testDefinitions)
    {
        if (!string.IsNullOrWhiteSpace(unitTestResult.TestId)
            && testDefinitions is not null
            && testDefinitions.TryGetValue(unitTestResult.TestId, out var testDefinition)
            && testDefinition.TestMethod is { ClassName: { Length: > 0 } className, Name: { Length: > 0 } methodName })
        {
            if (methodName.StartsWith($"{className}.", StringComparison.Ordinal))
            {
                return methodName;
            }

            return $"{className}.{methodName}";
        }

        return unitTestResult.TestName ?? string.Empty;
    }
}

[XmlRoot("TestRun", Namespace = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010")]
public class TestRun
{
    public Results? Results { get; set; }

    public ResultSummary? ResultSummary { get; set; }

    public Times? Times { get; set; }

    public TestDefinitions? TestDefinitions { get; set; }
}

public class Results
{
    [XmlElement("UnitTestResult")]
    public List<UnitTestResult>? UnitTestResults { get; set; }
}

public class Times
{
    [XmlAttribute("start")]
    public string? Start { get; set; }

    [XmlAttribute("finish")]
    public string? Finish { get; set; }

    [XmlAttribute("creation")]
    public string? Creation { get; set; }

    [XmlAttribute("queuing")]
    public string? Queuing { get; set; }
}

public class UnitTestResult
{
    [XmlAttribute("testId")]
    public string? TestId { get; set; }

    [XmlAttribute("testName")]
    public string? TestName { get; set; }

    [XmlAttribute("outcome")]
    public string? Outcome { get; set; }

    [XmlAttribute("startTime")]
    public string? StartTime { get; set; }

    [XmlAttribute("endTime")]
    public string? EndTime { get; set; }

    [XmlAttribute("duration")]
    public string? Duration { get; set; }

    public Output? Output { get; set; }
}

public class Output
{
    public ErrorInfo? ErrorInfo { get; set; }
    public string? StdOut { get; set; }

    [XmlIgnore]
    public string ErrorInfoString => string.Join(
        Environment.NewLine,
        new[] { ErrorMessage, StackTrace }.Where(static value => !string.IsNullOrWhiteSpace(value)));

    [XmlIgnore]
    public string? ErrorMessage => ErrorInfo?.Message;

    [XmlIgnore]
    public string? StackTrace => ErrorInfo?.StackTrace;
}

public class ErrorInfo
{
    public string? Message { get; set; }

    public string? StackTrace { get; set; }
}

public class ResultSummary
{
    public string? Outcome { get; set; }
    public Counters? Counters { get; set; }
}

public class Counters
{
    [XmlAttribute("total")]
    public int Total { get; set; }

    [XmlAttribute("executed")]
    public int Executed { get; set; }

    [XmlAttribute("passed")]
    public int Passed { get; set; }

    [XmlAttribute("failed")]
    public int Failed { get; set; }

    [XmlAttribute("error")]
    public int Error { get; set; }

    [XmlAttribute("timeout")]
    public int Timeout { get; set; }

    [XmlAttribute("aborted")]
    public int Aborted { get; set; }

    [XmlAttribute("inconclusive")]
    public int Inconclusive { get; set; }

    [XmlAttribute("passedButRunAborted")]
    public int PassedButRunAborted { get; set; }

    [XmlAttribute("notRunnable")]
    public int NotRunnable { get; set; }

    [XmlAttribute("notExecuted")]
    public int NotExecuted { get; set; }
}

public record TestResult(string Name, string Outcome, TimeSpan StartTime, TimeSpan EndTime, string? ErrorMessage = null, string? Stdout = null);

public record DetailedTestResult(string CanonicalName, string DisplayName, string Outcome, string? ErrorMessage = null, string? StackTrace = null, string? Stdout = null);

public class TestDefinitions
{
    [XmlElement("UnitTest")]
    public List<UnitTestDefinition>? UnitTests { get; set; }
}

public class UnitTestDefinition
{
    [XmlAttribute("id")]
    public string? Id { get; set; }

    [XmlAttribute("name")]
    public string? Name { get; set; }

    public TestMethodDefinition? TestMethod { get; set; }
}

public class TestMethodDefinition
{
    [XmlAttribute("className")]
    public string? ClassName { get; set; }

    [XmlAttribute("name")]
    public string? Name { get; set; }
}
