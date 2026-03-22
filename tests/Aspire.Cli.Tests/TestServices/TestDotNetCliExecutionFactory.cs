// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Caching;
using Aspire.Cli.Configuration;
using Aspire.Cli.DotNet;
using Aspire.Cli.Interaction;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Tests.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class TestDotNetCliExecutionFactory : IDotNetCliExecutionFactory
{
    private int _attemptCount;

    /// <summary>
    /// Gets or sets a callback that is invoked when <see cref="CreateExecution"/> is called.
    /// If this returns an <see cref="IDotNetCliExecution"/>, that execution is returned directly.
    /// </summary>
    public Func<string[], IDictionary<string, string>?, DirectoryInfo, DotNetCliRunnerInvocationOptions, IDotNetCliExecution>? CreateExecutionCallback { get; set; }

    /// <summary>
    /// Gets or sets an action that is invoked when <see cref="CreateExecution"/> is called,
    /// typically used for assertions on the arguments.
    /// </summary>
    public Action<string[], IDictionary<string, string>?, DirectoryInfo, DotNetCliRunnerInvocationOptions>? AssertionCallback { get; set; }

    /// <summary>
    /// Gets or sets a callback that is invoked for each execution attempt, receiving the attempt number (1-based)
    /// and options, and returning the exit code and optional stdout content.
    /// This is used for testing retry scenarios.
    /// </summary>
    public Func<int, DotNetCliRunnerInvocationOptions, (int ExitCode, string? Stdout)>? AttemptCallback { get; set; }

    /// <summary>
    /// When set, the execution will use this exit code when <see cref="IDotNetCliExecution.WaitForExitAsync"/> is called.
    /// </summary>
    public int DefaultExitCode { get; set; }

    /// <summary>
    /// When set, the interaction service that may be used to simulate DevKit extension behavior.
    /// </summary>
    public IInteractionService? InteractionService { get; set; }

    /// <summary>
    /// Gets the number of times <see cref="CreateExecution"/> has been called.
    /// </summary>
    public int AttemptCount => _attemptCount;

    public IDotNetCliExecution CreateExecution(string[] args, IDictionary<string, string>? env, DirectoryInfo workingDirectory, DotNetCliRunnerInvocationOptions options)
    {
        _attemptCount++;

        // Invoke assertion callback if set
        AssertionCallback?.Invoke(args, env, workingDirectory, options);

        // If a custom callback is provided, use it
        if (CreateExecutionCallback is not null)
        {
            return CreateExecutionCallback(args, env, workingDirectory, options);
        }

        // Use AttemptCallback if provided, otherwise create a simple callback that returns the default exit code
        var callback = AttemptCallback ?? ((_, _) => (DefaultExitCode, null));
        return new TestDotNetCliExecution(args, env, options, callback, () => _attemptCount);
    }
}

internal sealed class TestDotNetCliExecution : IDotNetCliExecution
{
    private readonly DotNetCliRunnerInvocationOptions _options;
    private readonly Func<int, DotNetCliRunnerInvocationOptions, (int ExitCode, string? Stdout)> _attemptCallback;
    private readonly Func<int> _attemptCounter;
    private bool _started;

    public TestDotNetCliExecution(
        string[] args,
        IDictionary<string, string>? env,
        DotNetCliRunnerInvocationOptions options,
        Func<int, DotNetCliRunnerInvocationOptions, (int ExitCode, string? Stdout)> attemptCallback,
        Func<int> attemptCounter)
    {
        Arguments = args;
        EnvironmentVariables = env?.ToDictionary(kvp => kvp.Key, kvp => (string?)kvp.Value)
            ?? new Dictionary<string, string?>();
        _options = options;
        _attemptCallback = attemptCallback;
        _attemptCounter = attemptCounter;
    }

    public string FileName => "dotnet";

    public IReadOnlyList<string> Arguments { get; }

    public IReadOnlyDictionary<string, string?> EnvironmentVariables { get; }

    public bool HasExited => false;

    public int ExitCode => 0;

    public bool Start()
    {
        _started = true;
        return true;
    }

    public Task<int> WaitForExitAsync(CancellationToken cancellationToken)
    {
        if (!_started)
        {
            throw new InvalidOperationException("Process has not been started.");
        }

        var attempt = _attemptCounter();
        var (exitCode, stdout) = _attemptCallback(attempt, _options);
        if (stdout is not null)
        {
            _options.StandardOutputCallback?.Invoke(stdout);
        }
        return Task.FromResult(exitCode);
    }
}

/// <summary>
/// Helper class for creating a <see cref="DotNetCliRunner"/> with a <see cref="TestDotNetCliExecutionFactory"/>
/// configured for assertion-based testing.
/// </summary>
internal static class DotNetCliRunnerTestHelper
{
    /// <summary>
    /// Creates a <see cref="DotNetCliRunner"/> with an assertion callback that is invoked on each execution.
    /// </summary>
    public static DotNetCliRunner Create(
        IServiceProvider serviceProvider,
        CliExecutionContext executionContext,
        Action<string[], IDictionary<string, string>?, DirectoryInfo, DotNetCliRunnerInvocationOptions> assertionCallback,
        int exitCode = 0,
        ILogger<DotNetCliRunner>? logger = null,
        AspireCliTelemetry? telemetry = null,
        IConfiguration? configuration = null,
        IDiskCache? diskCache = null)
    {
        var executionFactory = new TestDotNetCliExecutionFactory
        {
            AssertionCallback = assertionCallback,
            DefaultExitCode = exitCode
        };

        return new DotNetCliRunner(
            logger ?? serviceProvider.GetRequiredService<ILogger<DotNetCliRunner>>(),
            serviceProvider,
            telemetry ?? TestTelemetryHelper.CreateInitializedTelemetry(),
            configuration ?? serviceProvider.GetRequiredService<IConfiguration>(),
            diskCache ?? new NullDiskCache(),
            serviceProvider.GetRequiredService<IFeatures>(),
            serviceProvider.GetRequiredService<IInteractionService>(),
            executionContext,
            executionFactory);
    }

    /// <summary>
    /// Creates a <see cref="DotNetCliRunner"/> with an attempt callback for testing retry scenarios.
    /// Returns both the runner and the factory so the test can check <see cref="TestDotNetCliExecutionFactory.AttemptCount"/>.
    /// </summary>
    public static (DotNetCliRunner Runner, TestDotNetCliExecutionFactory ExecutionFactory) CreateWithRetry(
        IServiceProvider serviceProvider,
        CliExecutionContext executionContext,
        Func<int, DotNetCliRunnerInvocationOptions, (int ExitCode, string? Stdout)> attemptCallback,
        ILogger<DotNetCliRunner>? logger = null,
        AspireCliTelemetry? telemetry = null,
        IConfiguration? configuration = null,
        IDiskCache? diskCache = null)
    {
        var executionFactory = new TestDotNetCliExecutionFactory
        {
            AttemptCallback = attemptCallback
        };

        var runner = new DotNetCliRunner(
            logger ?? serviceProvider.GetRequiredService<ILogger<DotNetCliRunner>>(),
            serviceProvider,
            telemetry ?? TestTelemetryHelper.CreateInitializedTelemetry(),
            configuration ?? serviceProvider.GetRequiredService<IConfiguration>(),
            diskCache ?? new NullDiskCache(),
            serviceProvider.GetRequiredService<IFeatures>(),
            serviceProvider.GetRequiredService<IInteractionService>(),
            executionContext,
            executionFactory);

        return (runner, executionFactory);
    }
}
