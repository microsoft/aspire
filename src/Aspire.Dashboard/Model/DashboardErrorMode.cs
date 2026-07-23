// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Model;

/// <summary>
/// Manages the dashboard error mode state, which displays a splash screen with configuration errors
/// and blocks all HTTP requests until the user dismisses the errors.
/// </summary>
public sealed class DashboardErrorMode
{
    private readonly object _lock = new();
    private bool _isDismissed;

    /// <summary>
    /// Gets the list of validation failure messages that triggered error mode.
    /// </summary>
    public IReadOnlyList<string> ValidationFailures { get; }

    /// <summary>
    /// Gets whether the dashboard is in error mode (has validation failures).
    /// </summary>
    public bool IsErrorMode => ValidationFailures.Count > 0;

    /// <summary>
    /// Gets whether the user has dismissed the error mode to continue using the dashboard.
    /// </summary>
    public bool IsDismissed
    {
        get
        {
            lock (_lock)
            {
                return _isDismissed;
            }
        }
    }

    /// <summary>
    /// Gets whether error mode should block dashboard functionality (has errors and not dismissed).
    /// </summary>
    public bool ShouldBlock => IsErrorMode && !IsDismissed;

    public DashboardErrorMode(IReadOnlyList<string> validationFailures)
    {
        ValidationFailures = validationFailures ?? Array.Empty<string>();
    }

    /// <summary>
    /// Dismisses the error mode, allowing the user to continue using the dashboard despite errors.
    /// </summary>
    public void Dismiss()
    {
        lock (_lock)
        {
            _isDismissed = true;
        }
    }
}
