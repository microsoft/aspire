// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace TestSelector;

/// <summary>
/// Provides structured diagnostic output for verbose logging during test selection.
/// All output goes to stderr to not interfere with JSON output on stdout.
/// </summary>
public sealed class DiagnosticLogger
{
    private readonly bool _enabled;
    private int _stepNumber;

    public DiagnosticLogger(bool enabled)
    {
        _enabled = enabled;
    }

    /// <summary>
    /// Whether verbose logging is enabled.
    /// </summary>
    public bool IsEnabled => _enabled;

    /// <summary>
    /// Logs a section header for a major processing step.
    /// </summary>
    public void LogStep(string title)
    {
        if (!_enabled)
        {
            return;
        }

        _stepNumber++;
        Console.Error.WriteLine();
        Console.Error.WriteLine($"═══ Step {_stepNumber}: {title} ═══");
    }

    /// <summary>
    /// Logs an informational message.
    /// </summary>
    public void LogInfo(string message)
    {
        if (!_enabled)
        {
            return;
        }

        Console.Error.WriteLine($"  ℹ {message}");
    }

    /// <summary>
    /// Logs a success/positive outcome.
    /// </summary>
    public void LogSuccess(string message)
    {
        if (!_enabled)
        {
            return;
        }

        Console.Error.WriteLine($"  ✓ {message}");
    }

    /// <summary>
    /// Logs a warning or notable condition.
    /// </summary>
    public void LogWarning(string message)
    {
        if (!_enabled)
        {
            return;
        }

        Console.Error.WriteLine($"  ⚠ {message}");
    }

    /// <summary>
    /// Logs a match between a file and a rule/pattern.
    /// </summary>
    public void LogMatch(string file, string rule, string? detail = null)
    {
        if (!_enabled)
        {
            return;
        }

        var detailStr = detail != null ? $" → {detail}" : "";
        Console.Error.WriteLine($"    • {file}");
        Console.Error.WriteLine($"      matched: {rule}{detailStr}");
    }

    /// <summary>
    /// Logs a list of items with a header.
    /// </summary>
    public void LogList(string header, IEnumerable<string> items)
    {
        if (!_enabled)
        {
            return;
        }

        var itemList = items.ToList();
        Console.Error.WriteLine($"  {header} ({itemList.Count}):");

        foreach (var item in itemList)
        {
            Console.Error.WriteLine($"    • {item}");
        }
    }

    /// <summary>
    /// Logs a dictionary/mapping with a header.
    /// </summary>
    public void LogMapping(string header, IEnumerable<KeyValuePair<string, string>> mappings)
    {
        if (!_enabled)
        {
            return;
        }

        var mappingList = mappings.ToList();
        Console.Error.WriteLine($"  {header} ({mappingList.Count}):");

        foreach (var (key, value) in mappingList)
        {
            Console.Error.WriteLine($"    • {key} → {value}");
        }
    }

    /// <summary>
    /// Logs category trigger status.
    /// </summary>
    public void LogCategories(string header, Dictionary<string, bool> categories)
    {
        if (!_enabled)
        {
            return;
        }

        var enabled = categories.Where(c => c.Value).Select(c => c.Key).ToList();
        var disabled = categories.Where(c => !c.Value).Select(c => c.Key).ToList();

        Console.Error.WriteLine($"  {header}:");
        if (enabled.Count > 0)
        {
            Console.Error.WriteLine($"    Triggered: {string.Join(", ", enabled)}");
        }
        if (disabled.Count > 0)
        {
            Console.Error.WriteLine($"    Not triggered: {string.Join(", ", disabled)}");
        }
    }

    /// <summary>
    /// Logs a decision/outcome with explanation.
    /// </summary>
    public void LogDecision(string decision, string reason)
    {
        if (!_enabled)
        {
            return;
        }

        Console.Error.WriteLine();
        Console.Error.WriteLine($"  ▶ Decision: {decision}");
        Console.Error.WriteLine($"    Reason: {reason}");
    }

    /// <summary>
    /// Logs the final summary of test selection.
    /// </summary>
    public void LogSummary(bool runAll, string reason, int testProjectCount, IEnumerable<string> testProjects)
    {
        if (!_enabled)
        {
            return;
        }

        Console.Error.WriteLine();
        Console.Error.WriteLine("═══ Summary ═══");
        Console.Error.WriteLine($"  Run all tests: {runAll}");
        Console.Error.WriteLine($"  Reason: {reason}");

        if (!runAll && testProjectCount > 0)
        {
            var projects = testProjects.ToList();
            Console.Error.WriteLine($"  Test projects to run ({projects.Count}):");
            foreach (var project in projects)
            {
                Console.Error.WriteLine($"    • {project}");
            }
        }

        Console.Error.WriteLine();
    }

    /// <summary>
    /// Logs a sub-section header.
    /// </summary>
    public void LogSubSection(string title)
    {
        if (!_enabled)
        {
            return;
        }

        Console.Error.WriteLine($"  --- {title} ---");
    }
}
