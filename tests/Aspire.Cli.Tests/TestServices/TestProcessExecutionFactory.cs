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

internal sealed class TestProcessExecutionFactory : IProcessExecutionFactory
{
    private int _attemptCount;

    /// <summary>
    /// Gets or sets a callback that is invoked when <see cref="CreateExecution"/> is called.
    /// If this returns an <see cref="IProcessExecution"/>, that execution is returned directly.
    /// </summary>
    public Func<string[], IDictionary<string, string>?, DirectoryInfo, ProcessInvocationOptions, IProcessExecution>? CreateExecutionCallback { get; set; }

    public Func<string, string[], IDictionary<string, string>?, DirectoryInfo, ProcessInvocationOptions, IProcessExecution>? CreateExecutionWithFileNameCallback { get; set; }

    /// <summary>
    /// Gets or sets an action that is invoked when <see cref="CreateExecution"/> is called,
    /// typically used for assertions on the arguments.
    /// </summary>
    public Action<string[], IDictionary<string, string>?, DirectoryInfo, ProcessInvocationOptions>? AssertionCallback { get; set; }

    public Action<string, string[], IDictionary<string, string>?, DirectoryInfo, ProcessInvocationOptions>? FileNameAssertionCallback { get; set; }

    /// <summary>
    /// Gets or sets a callback that is invoked for each execution attempt, receiving the attempt number (1-based)
    /// and options, and returning the exit code and optional stdout content.
    /// This is used for testing retry scenarios.
    /// </summary>
    public Func<int, ProcessInvocationOptions, (int ExitCode, string? Stdout)>? AttemptCallback { get; set; }

    /// <summary>
    /// Gets or sets an async callback that is invoked for each execution attempt, receiving the attempt number (1-based)
    /// and options, and returning the exit code and optional stdout content.
    /// </summary>
    public Func<int, ProcessInvocationOptions, CancellationToken, Task<(int ExitCode, string? Stdout)>>? AsyncAttemptCallback { get; set; }

    /// <summary>
    /// When set, the execution will use this exit code when <see cref="IProcessExecution.WaitForExitAsync"/> is called.
    /// </summary>
    public int DefaultExitCode { get; set; }

    /// <summary>
    /// When set, the interaction service that may be used to simulate DevKit extension behavior.
    /// </summary>
    public IInteractionService? InteractionService { get; set; }

    public List<IProcessExecution> CreatedExecutions { get; } = [];

    public string? LastFileName { get; private set; }

    public string[]? LastArguments { get; private set; }

    public IDictionary<string, string>? LastEnvironmentVariables { get; private set; }

    public DirectoryInfo? LastWorkingDirectory { get; private set; }

    public ProcessInvocationOptions? LastProcessInvocationOptions { get; private set; }

    /// <summary>
    /// Gets the number of times <see cref="CreateExecution"/> has been called.
    /// </summary>
    public int AttemptCount => _attemptCount;

    public IProcessExecution CreateExecution(string fileName, string[] args, IDictionary<string, string>? env, DirectoryInfo workingDirectory, ProcessInvocationOptions options)
    {
        _attemptCount++;
        LastFileName = fileName;
        LastArguments = args;
        LastEnvironmentVariables = env;
        LastWorkingDirectory = workingDirectory;
        LastProcessInvocationOptions = options;

        // Invoke assertion callback if set
        AssertionCallback?.Invoke(args, env, workingDirectory, options);
        FileNameAssertionCallback?.Invoke(fileName, args, env, workingDirectory, options);

        if (CreateExecutionWithFileNameCallback is not null)
        {
            var execution = CreateExecutionWithFileNameCallback(fileName, args, env, workingDirectory, options);
            CreatedExecutions.Add(execution);
            return execution;
        }

        // If a custom callback is provided, use it
        if (CreateExecutionCallback is not null)
        {
            var execution = CreateExecutionCallback(args, env, workingDirectory, options);
            CreatedExecutions.Add(execution);
            return execution;
        }

        var asyncAttemptCallback = AsyncAttemptCallback;
        var attemptCallback = AttemptCallback;
        var callback = asyncAttemptCallback ??
            (attemptCallback is not null
                ? (attempt, options, _) => Task.FromResult(attemptCallback(attempt, options))
                : (_, _, _) => Task.FromResult((DefaultExitCode, (string?)null)));
        var testExecution = new TestProcessExecution(fileName, args, env, options, callback, () => _attemptCount);
        CreatedExecutions.Add(testExecution);
        return testExecution;
    }
}

internal sealed class TestProcessExecution : IProcessExecution
{
    private readonly ProcessInvocationOptions _options;
    private readonly Func<int, ProcessInvocationOptions, CancellationToken, Task<(int ExitCode, string? Stdout)>> _attemptCallback;
    private readonly Func<int> _attemptCounter;
    private bool _started;
    private bool _hasExited;
    private int _exitCode;

    public TestProcessExecution(
        string fileName,
        string[] args,
        IDictionary<string, string>? env,
        ProcessInvocationOptions options,
        Func<int, ProcessInvocationOptions, CancellationToken, Task<(int ExitCode, string? Stdout)>> attemptCallback,
        Func<int> attemptCounter)
    {
        FileName = fileName;
        Arguments = args;
        EnvironmentVariables = env?.ToDictionary(kvp => kvp.Key, kvp => (string?)kvp.Value)
            ?? new Dictionary<string, string?>();
        _options = options;
        _attemptCallback = attemptCallback;
        _attemptCounter = attemptCounter;
    }

    public string FileName { get; }

    public IReadOnlyList<string> Arguments { get; }

    public IReadOnlyDictionary<string, string?> EnvironmentVariables { get; }

    public bool Started => _started;

    public bool HasExited => _hasExited;

    public int ExitCode => _exitCode;

    public int ProcessId { get; init; } = Environment.ProcessId;

    public bool StartReturnValue { get; init; } = true;

    public Func<ProcessInvocationOptions, CancellationToken, Task<int>>? WaitForExitAsyncCallback { get; init; }

    public Action<bool>? KillCallback { get; init; }

    public Action? DisposeCallback { get; init; }

    public int KillCount { get; private set; }

    public bool? KilledEntireProcessTree { get; private set; }

    public int DisposeCount { get; private set; }

    public bool Start()
    {
        if (!StartReturnValue)
        {
            return false;
        }

        _started = true;
        return true;
    }

    public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
    {
        if (!_started)
        {
            throw new InvalidOperationException("Process has not been started.");
        }

        if (WaitForExitAsyncCallback is not null)
        {
            _exitCode = await WaitForExitAsyncCallback(_options, cancellationToken).ConfigureAwait(false);
            _hasExited = true;
            return _exitCode;
        }

        var attempt = _attemptCounter();
        var (exitCode, stdout) = await _attemptCallback(attempt, _options, cancellationToken).ConfigureAwait(false);
        _exitCode = exitCode;
        _hasExited = true;
        if (stdout is not null)
        {
            _options.StandardOutputCallback?.Invoke(stdout);
        }
        return _exitCode;
    }

    public void Kill(bool entireProcessTree)
    {
        KillCount++;
        KilledEntireProcessTree = entireProcessTree;
        KillCallback?.Invoke(entireProcessTree);
    }

    public void Dispose()
    {
        DisposeCount++;
        DisposeCallback?.Invoke();
    }
}

/// <summary>
/// Helper class for creating a <see cref="DotNetCliRunner"/> with a <see cref="TestProcessExecutionFactory"/>
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
        Action<string[], IDictionary<string, string>?, DirectoryInfo, ProcessInvocationOptions> assertionCallback,
        int exitCode = 0,
        ILogger<DotNetCliRunner>? logger = null,
        AspireCliTelemetry? telemetry = null,
        IConfiguration? configuration = null,
        IDiskCache? diskCache = null)
    {
        var executionFactory = new TestProcessExecutionFactory
        {
            AssertionCallback = assertionCallback,
            DefaultExitCode = exitCode
        };
        var resolvedConfiguration = configuration ?? serviceProvider.GetRequiredService<IConfiguration>();

        return new DotNetCliRunner(
            logger ?? serviceProvider.GetRequiredService<ILogger<DotNetCliRunner>>(),
            serviceProvider,
            telemetry ?? TestTelemetryHelper.CreateInitializedTelemetry(),
            serviceProvider.GetRequiredService<ProfilingTelemetry>(),
            resolvedConfiguration,
            diskCache ?? new NullDiskCache(),
            serviceProvider.GetRequiredService<IFeatures>(),
            serviceProvider.GetRequiredService<IInteractionService>(),
            executionContext,
            executionFactory);
    }

    public static DotNetCliRunner Create(
        IServiceProvider serviceProvider,
        CliExecutionContext executionContext,
        Action<string, string[], IDictionary<string, string>?, DirectoryInfo, ProcessInvocationOptions> assertionCallback,
        int exitCode = 0,
        ILogger<DotNetCliRunner>? logger = null,
        AspireCliTelemetry? telemetry = null,
        IConfiguration? configuration = null,
        IDiskCache? diskCache = null)
    {
        var executionFactory = new TestProcessExecutionFactory
        {
            FileNameAssertionCallback = assertionCallback,
            DefaultExitCode = exitCode
        };
        var resolvedConfiguration = configuration ?? serviceProvider.GetRequiredService<IConfiguration>();

        return new DotNetCliRunner(
            logger ?? serviceProvider.GetRequiredService<ILogger<DotNetCliRunner>>(),
            serviceProvider,
            telemetry ?? TestTelemetryHelper.CreateInitializedTelemetry(),
            serviceProvider.GetRequiredService<ProfilingTelemetry>(),
            resolvedConfiguration,
            diskCache ?? new NullDiskCache(),
            serviceProvider.GetRequiredService<IFeatures>(),
            serviceProvider.GetRequiredService<IInteractionService>(),
            executionContext,
            executionFactory);
    }

    /// <summary>
    /// Creates a <see cref="DotNetCliRunner"/> with an attempt callback for testing retry scenarios.
    /// Returns both the runner and the factory so the test can check <see cref="TestProcessExecutionFactory.AttemptCount"/>.
    /// </summary>
    public static (DotNetCliRunner Runner, TestProcessExecutionFactory ExecutionFactory) CreateWithRetry(
        IServiceProvider serviceProvider,
        CliExecutionContext executionContext,
        Func<int, ProcessInvocationOptions, (int ExitCode, string? Stdout)> attemptCallback,
        ILogger<DotNetCliRunner>? logger = null,
        AspireCliTelemetry? telemetry = null,
        IConfiguration? configuration = null,
        IDiskCache? diskCache = null)
    {
        var executionFactory = new TestProcessExecutionFactory
        {
            AttemptCallback = attemptCallback
        };
        var resolvedConfiguration = configuration ?? serviceProvider.GetRequiredService<IConfiguration>();

        var runner = new DotNetCliRunner(
            logger ?? serviceProvider.GetRequiredService<ILogger<DotNetCliRunner>>(),
            serviceProvider,
            telemetry ?? TestTelemetryHelper.CreateInitializedTelemetry(),
            serviceProvider.GetRequiredService<ProfilingTelemetry>(),
            resolvedConfiguration,
            diskCache ?? new NullDiskCache(),
            serviceProvider.GetRequiredService<IFeatures>(),
            serviceProvider.GetRequiredService<IInteractionService>(),
            executionContext,
            executionFactory);

        return (runner, executionFactory);
    }
}
