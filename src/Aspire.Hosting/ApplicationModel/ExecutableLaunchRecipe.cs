// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPROJECTS001
#pragma warning disable ASPIREEXTENSION001

using System.Text.Json;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.Configuration;

namespace Aspire.Hosting.ApplicationModel;

internal interface IExecutableLaunchRecipe
{
    Task<ExecutableLaunchPlan> CreateLaunchPlanAsync(ExecutableLaunchContext context);
}

internal sealed class ExecutableLaunchRecipeAnnotation(IExecutableLaunchRecipe recipe) : IResourceAnnotation
{
    public IExecutableLaunchRecipe Recipe { get; } = recipe ?? throw new ArgumentNullException(nameof(recipe));
}

internal enum ExecutableLaunchMechanism
{
    Process,
    Ide
}

internal enum ExecutableLaunchArgumentRole
{
    LaunchTool,
    LaunchProfile,
    Application,
    ToolOption
}

internal sealed class ExecutableLaunchDecision(
    ExecutableLaunchMechanism mechanism,
    string launchMode,
    SupportsDebuggingAnnotation? debugSupport = null,
    bool useCompatibilityProjectLaunchConfiguration = false,
    string? projectLaunchMode = null)
{
    public ExecutableLaunchMechanism Mechanism { get; } = mechanism;

    public string LaunchMode { get; } = launchMode ?? throw new ArgumentNullException(nameof(launchMode));

    public string ProjectLaunchMode { get; } = projectLaunchMode ?? launchMode;

    public SupportsDebuggingAnnotation? DebugSupport { get; } = debugSupport;

    public bool UseCompatibilityProjectLaunchConfiguration { get; } = useCompatibilityProjectLaunchConfiguration;
}

internal sealed class ExecutableLaunchContext(
    IResource resource,
    IConfiguration configuration,
    DistributedApplicationOptions distributedApplicationOptions,
    IExecutionConfigurationResult executionConfiguration,
    ExecutableLaunchDecision decision,
    CancellationToken cancellationToken)
{
    public IResource Resource { get; } = resource ?? throw new ArgumentNullException(nameof(resource));

    public IConfiguration Configuration { get; } = configuration ?? throw new ArgumentNullException(nameof(configuration));

    public DistributedApplicationOptions DistributedApplicationOptions { get; } = distributedApplicationOptions ?? throw new ArgumentNullException(nameof(distributedApplicationOptions));

    public IExecutionConfigurationResult ExecutionConfiguration { get; } = executionConfiguration ?? throw new ArgumentNullException(nameof(executionConfiguration));

    public ExecutableLaunchDecision Decision { get; } = decision ?? throw new ArgumentNullException(nameof(decision));

    public CancellationToken CancellationToken { get; } = cancellationToken;
}

internal sealed class ExecutableLaunchPlan(
    string command,
    string workingDirectory,
    ExecutableLaunchMechanism mechanism,
    IReadOnlyList<string>? arguments,
    IEnumerable<KeyValuePair<string, string>> environmentVariables,
    IEnumerable<JsonElement> launchConfigurations,
    IEnumerable<ExecutableLaunchArgument> displayArguments)
{
    public string Command { get; } = !string.IsNullOrWhiteSpace(command)
        ? command
        : throw new ArgumentException("The executable command cannot be null, empty, or whitespace.", nameof(command));

    public string WorkingDirectory { get; } = workingDirectory ?? throw new ArgumentNullException(nameof(workingDirectory));

    public ExecutableLaunchMechanism Mechanism { get; } = mechanism;

    public IReadOnlyList<string>? Arguments { get; } = arguments?.ToArray();

    public IReadOnlyList<KeyValuePair<string, string>> EnvironmentVariables { get; } = environmentVariables.ToArray();

    public IReadOnlyList<JsonElement> LaunchConfigurations { get; } = launchConfigurations.ToArray();

    public IReadOnlyList<ExecutableLaunchArgument> DisplayArguments { get; } = displayArguments.ToArray();
}

internal sealed class ExecutableLaunchArgument(
    string value,
    bool isSensitive,
    bool executable,
    bool display,
    int? effectiveArgumentIndex,
    ExecutableLaunchArgumentRole role)
{
    public string Value { get; } = value ?? throw new ArgumentNullException(nameof(value));

    public bool IsSensitive { get; } = isSensitive;

    public bool Executable { get; } = executable;

    public bool Display { get; } = display;

    public int? EffectiveArgumentIndex { get; } = effectiveArgumentIndex;

    public ExecutableLaunchArgumentRole Role { get; } = role;

    public ExecutableLaunchArgument WithEffectiveArgumentIndex(int? effectiveArgumentIndex) =>
        new(Value, IsSensitive, Executable, Display, effectiveArgumentIndex, Role);
}

internal sealed class DirectExecutableLaunchRecipe : IExecutableLaunchRecipe
{
    public static DirectExecutableLaunchRecipe Instance { get; } = new();

    private DirectExecutableLaunchRecipe()
    {
    }

    public async Task<ExecutableLaunchPlan> CreateLaunchPlanAsync(ExecutableLaunchContext context)
    {
        var resource = (ExecutableResource)context.Resource;
        var arguments = context.ExecutionConfiguration.Arguments.ToList();
        var launchToolArgumentsData = context.ExecutionConfiguration.AdditionalConfigurationData
            .OfType<LaunchToolArgumentsData>()
            .FirstOrDefault();
        var launchToolArgumentCount = launchToolArgumentsData?.Count ?? 0;
        var omitLaunchToolArguments =
            context.Decision.Mechanism == ExecutableLaunchMechanism.Ide &&
            context.Decision.DebugSupport is { } activeDebugSupport &&
            resource.TryGetLastAnnotation<LaunchToolArgsCallbackAnnotation>(out var launchToolAnnotation) &&
            string.Equals(
                launchToolAnnotation.OwningLaunchConfigurationType,
                activeDebugSupport.LaunchConfigurationType,
                StringComparison.Ordinal);
        var omittedLaunchToolArgumentCount = omitLaunchToolArguments ? launchToolArgumentCount : 0;

        var executableArguments = new List<string>(arguments.Count - omittedLaunchToolArgumentCount);
        var displayArguments = new List<ExecutableLaunchArgument>(arguments.Count);
        var nextExecutableArgumentIndex = 0;

        for (var i = 0; i < arguments.Count; i++)
        {
            var argument = arguments[i];
            var isLaunchToolArgument = i < launchToolArgumentCount;
            var executable = i >= omittedLaunchToolArgumentCount;
            var display = launchToolArgumentsData?.ShowInCommandLine is not false || !isLaunchToolArgument;
            var effectiveArgumentIndex = executable ? nextExecutableArgumentIndex++ : (int?)null;

            if (executable)
            {
                executableArguments.Add(argument.Value);
            }

            if (display)
            {
                displayArguments.Add(new(
                    argument.Value,
                    argument.IsSensitive,
                    executable,
                    display,
                    effectiveArgumentIndex,
                    isLaunchToolArgument ? ExecutableLaunchArgumentRole.LaunchTool : ExecutableLaunchArgumentRole.Application));
            }
        }

        var launchConfigurations = await CreateLaunchConfigurationsAsync(context).ConfigureAwait(false);

        return new(
            resource.Command,
            resource.WorkingDirectory,
            context.Decision.Mechanism,
            executableArguments.Count > 0 ? executableArguments : null,
            context.ExecutionConfiguration.EnvironmentVariables,
            launchConfigurations,
            displayArguments);
    }

    private static async Task<IReadOnlyList<JsonElement>> CreateLaunchConfigurationsAsync(ExecutableLaunchContext context)
    {
        if (context.Decision.DebugSupport is not { } debugSupport)
        {
            return [];
        }

        if (debugSupport.LaunchConfigurationType is KnownLaunchConfigurationTypes.Project &&
            !context.Resource.TryGetProjectMetadata(out _))
        {
            throw new FailedToApplyEnvironmentException(
                $"Resource '{context.Resource.Name}' declares \"project\" debug launch support (WithDebugSupport) but has no project metadata. " +
                $"The \"project\" launch configuration type is reserved for .NET project resources; use a resource that carries {nameof(IProjectMetadata)} or a different launch configuration type.");
        }

        var launchConfiguration = await ProduceLaunchConfigurationAsync(context, debugSupport).ConfigureAwait(false);
        return [launchConfiguration];
    }

    internal static async Task<JsonElement> ProduceLaunchConfigurationAsync(
        ExecutableLaunchContext context,
        SupportsDebuggingAnnotation debugSupport)
    {
        var callbackContext = new LaunchConfigurationCallbackContext(
            context.Decision.LaunchMode,
            context.Resource,
            context.ExecutionConfiguration.EnvironmentVariables.ToDictionary(
                static variable => variable.Key,
                static variable => variable.Value,
                StringComparer.Ordinal),
            context.CancellationToken);
        var launchConfiguration = await debugSupport.LaunchConfigurationProducer(callbackContext).ConfigureAwait(false);

        return JsonSerializer.SerializeToElement(launchConfiguration, launchConfiguration.GetType());
    }
}

internal sealed class ProjectExecutableLaunchRecipe : IExecutableLaunchRecipe
{
    public static ProjectExecutableLaunchRecipe Instance { get; } = new();

    private ProjectExecutableLaunchRecipe()
    {
    }

    public async Task<ExecutableLaunchPlan> CreateLaunchPlanAsync(ExecutableLaunchContext context)
    {
        var resource = (ProjectResource)context.Resource;
        if (!resource.TryGetProjectMetadata(out var projectMetadata))
        {
            throw new InvalidOperationException($"Project resource '{resource.Name}' is missing required metadata.");
        }

        resource.TryGetLastAnnotation<ExecutableAnnotation>(out var executableAnnotation);
        resource.TryGetLastAnnotation<ProjectLaunchArgsOverrideAnnotation>(out var launchOverride);

        var command = executableAnnotation?.Command ?? "dotnet";
        var workingDirectory = executableAnnotation?.WorkingDirectory ?? Path.GetDirectoryName(projectMetadata.ProjectPath) ?? string.Empty;
        var projectArguments = new List<string>();

        if (context.Decision.Mechanism == ExecutableLaunchMechanism.Process)
        {
            if (launchOverride is not null)
            {
                launchOverride.Apply(projectArguments, projectMetadata.ProjectPath, context.DistributedApplicationOptions.Configuration);
            }
            else if (executableAnnotation is null)
            {
                AddDefaultProjectProcessArguments(
                    projectArguments,
                    projectMetadata,
                    context.Configuration,
                    context.DistributedApplicationOptions.Configuration);
            }
        }

        var launchConfigurations = await CreateLaunchConfigurationsAsync(
            context,
            resource,
            projectMetadata,
            executableAnnotation,
            launchOverride).ConfigureAwait(false);
        var projectLaunchConfigurationHandlesLaunchProfile =
            context.Decision.Mechanism == ExecutableLaunchMechanism.Ide &&
            launchConfigurations.Any(IsProjectLaunchConfiguration);

        var launchToolArgumentsData = context.ExecutionConfiguration.AdditionalConfigurationData
            .OfType<LaunchToolArgumentsData>()
            .FirstOrDefault();
        var launchToolArgumentCount = launchToolArgumentsData?.Count ?? 0;
        if (launchToolArgumentCount > 0 || projectLaunchConfigurationHandlesLaunchProfile)
        {
            // Generated project arguments describe a Process invocation. A resolved launch-tool prefix replaces
            // that invocation, while an IDE project launch owns it entirely. Runtime Process fallback is not used,
            // so there is no reason to retain a second candidate command in either case.
            projectArguments.Clear();
        }

        var omittedLaunchToolArgumentCount =
            context.Decision.Mechanism == ExecutableLaunchMechanism.Ide &&
            context.Decision.DebugSupport is { } activeDebugSupport &&
            resource.TryGetLastAnnotation<LaunchToolArgsCallbackAnnotation>(out var launchToolAnnotation) &&
            string.Equals(
                launchToolAnnotation.OwningLaunchConfigurationType,
                activeDebugSupport.LaunchConfigurationType,
                StringComparison.Ordinal)
                ? launchToolArgumentCount
                : 0;

        var executableArgumentStartIndex = projectArguments.Count;
        var (launchArguments, dotnetProjectLaunchArgumentIndex) = BuildLaunchArguments(
            resource,
            context.Decision.Mechanism,
            projectLaunchConfigurationHandlesLaunchProfile,
            context.ExecutionConfiguration.Arguments,
            executableArgumentStartIndex,
            launchToolArgumentCount,
            omittedLaunchToolArgumentCount,
            launchToolArgumentsData?.ShowInCommandLine ?? true,
            launchOverride);

        if (launchToolArgumentCount > 0 || launchOverride is null)
        {
            AddDotnetProjectLaunchArguments(
                launchArguments,
                dotnetProjectLaunchArgumentIndex,
                executableArgumentStartIndex,
                context.DistributedApplicationOptions.Configuration);
        }

        projectArguments.AddRange(launchArguments.Where(static argument => argument.Executable).Select(static argument => argument.Value));

        return new(
            command,
            workingDirectory,
            context.Decision.Mechanism,
            projectArguments.Count > 0 ? projectArguments : null,
            context.ExecutionConfiguration.EnvironmentVariables,
            launchConfigurations,
            launchArguments.Where(static argument => argument.Display));
    }

    private static async Task<IReadOnlyList<JsonElement>> CreateLaunchConfigurationsAsync(
        ExecutableLaunchContext context,
        ProjectResource resource,
        IProjectMetadata projectMetadata,
        ExecutableAnnotation? executableAnnotation,
        ProjectLaunchArgsOverrideAnnotation? launchOverride)
    {
        var launchConfigurations = new List<JsonElement>();

        if (launchOverride is not null)
        {
            launchConfigurations.Add(JsonSerializer.SerializeToElement(
                ProjectLaunchConfigurationFactory.Create(resource, projectMetadata, context.Decision.ProjectLaunchMode)));

            if (context.Decision.DebugSupport is { LaunchConfigurationType: not KnownLaunchConfigurationTypes.Project } customDebugSupport)
            {
                launchConfigurations.Add(await DirectExecutableLaunchRecipe
                    .ProduceLaunchConfigurationAsync(context, customDebugSupport)
                    .ConfigureAwait(false));
            }

            return launchConfigurations;
        }

        if (context.Decision.DebugSupport is { } debugSupport)
        {
            launchConfigurations.Add(await DirectExecutableLaunchRecipe
                .ProduceLaunchConfigurationAsync(context, debugSupport)
                .ConfigureAwait(false));
            return launchConfigurations;
        }

        if (context.Decision.UseCompatibilityProjectLaunchConfiguration)
        {
            launchConfigurations.Add(JsonSerializer.SerializeToElement(
                ProjectLaunchConfigurationFactory.Create(resource, projectMetadata, context.Decision.ProjectLaunchMode)));
            return launchConfigurations;
        }

        if (context.Decision.Mechanism == ExecutableLaunchMechanism.Process && executableAnnotation is null)
        {
            // Keep project metadata on process-launched legacy projects for existing DCP/dashboard consumers.
            launchConfigurations.Add(JsonSerializer.SerializeToElement(new ProjectLaunchConfiguration
            {
                ProjectPath = projectMetadata.ProjectPath
            }));
        }

        return launchConfigurations;
    }

    private static bool IsProjectLaunchConfiguration(JsonElement launchConfiguration) =>
        launchConfiguration.ValueKind == JsonValueKind.Object &&
        launchConfiguration.TryGetProperty("type", out var type) &&
        type.ValueKind == JsonValueKind.String &&
        type.GetString() is KnownLaunchConfigurationTypes.Project;

    private static void AddDefaultProjectProcessArguments(
        List<string> projectArguments,
        IProjectMetadata projectMetadata,
        IConfiguration configuration,
        string? appHostConfiguration)
    {
        // `dotnet watch` does not work with file-based apps yet, so use `dotnet run` in that case.
        if (configuration.GetBool("DOTNET_WATCH") is not true || projectMetadata.IsFileBasedApp)
        {
            projectArguments.Add("run");
            projectArguments.Add(projectMetadata.IsFileBasedApp ? "--file" : "--project");
            projectArguments.Add(projectMetadata.ProjectPath);
            if (projectMetadata.IsFileBasedApp)
            {
                projectArguments.Add("--no-cache");
            }
            if (projectMetadata.SuppressBuild)
            {
                projectArguments.Add("--no-build");
            }
        }
        else
        {
            projectArguments.AddRange([
                "watch",
                "--non-interactive",
                "--no-hot-reload",
                "--project",
                projectMetadata.ProjectPath
            ]);
        }

        if (!string.IsNullOrEmpty(appHostConfiguration))
        {
            projectArguments.AddRange(["--configuration", appHostConfiguration]);
        }

        // Aspire already materializes launch-profile settings into the application model, so allowing `dotnet`
        // to apply the profile again would let it override the resolved environment.
        projectArguments.Add("--no-launch-profile");
    }

    private static (List<ExecutableLaunchArgument> LaunchArguments, int? DotnetProjectLaunchArgumentIndex) BuildLaunchArguments(
        ProjectResource resource,
        ExecutableLaunchMechanism mechanism,
        bool projectLaunchConfigurationHandlesLaunchProfile,
        IEnumerable<(string Value, bool IsSensitive)> appHostArguments,
        int executableArgumentStartIndex,
        int launchToolArgumentCount,
        int omittedLaunchToolArgumentCount,
        bool showLaunchToolArgumentsInCommandLine,
        ProjectLaunchArgsOverrideAnnotation? projectLaunchArgsOverride)
    {
        var appHostArgumentList = appHostArguments.ToList();
        var useProjectLaunchArgsOverride = projectLaunchArgsOverride is not null && launchToolArgumentCount == 0;
        if (useProjectLaunchArgsOverride &&
            projectLaunchArgsOverride?.LeadingResourceArgumentToRemove is { } leadingResourceArgumentToRemove &&
            appHostArgumentList.Count > 0 &&
            string.Equals(appHostArgumentList[0].Value, leadingResourceArgumentToRemove, StringComparison.Ordinal))
        {
            // MAUI keeps an SDK-shaped `run` argument for model consumers while its explicit project launch
            // override already supplies the real verb. Remove only the declared duplicate.
            appHostArgumentList.RemoveAt(0);
            launchToolArgumentCount = Math.Max(0, launchToolArgumentCount - 1);
            omittedLaunchToolArgumentCount = Math.Max(0, omittedLaunchToolArgumentCount - 1);
        }

        var dotnetProjectLaunchResourceArgumentIndex = FindExecutableAnnotatedDotnetProjectLaunchArgument(
            resource,
            appHostArgumentList);
        var dotnetProjectApplicationArgumentBoundaryIndex =
            dotnetProjectLaunchResourceArgumentIndex is { } boundarySearchStartIndex
                ? appHostArgumentList.FindIndex(
                    boundarySearchStartIndex + 1,
                    static argument => string.Equals(argument.Value, "--", StringComparison.Ordinal))
                : -1;
        var launchArguments = new List<ExecutableLaunchArgument>();
        int? dotnetProjectLaunchArgumentIndex = null;
        var nextExecutableArgumentIndex = executableArgumentStartIndex;
        List<string>? projectLaunchProfileArguments = null;
        var includeProfileArgumentsInSpec = false;

        ExecutableLaunchArgument CreateLaunchArgument(
            string value,
            bool isSensitive,
            bool executable,
            bool display,
            ExecutableLaunchArgumentRole role)
        {
            var effectiveArgumentIndex = executable ? nextExecutableArgumentIndex++ : (int?)null;
            return new(value, isSensitive, executable, display, effectiveArgumentIndex, role);
        }

        if (!useProjectLaunchArgsOverride)
        {
            var ordinaryAppHostArgumentCount = Math.Max(0, appHostArgumentList.Count - launchToolArgumentCount);

            // A project IDE launch delegates profile arguments to the IDE unless there are no ordinary AppHost
            // arguments, in which case the profile arguments are still shown in the dashboard. Process and custom
            // IDE launches materialize them into the selected invocation.
            if (mechanism == ExecutableLaunchMechanism.Process ||
                !projectLaunchConfigurationHandlesLaunchProfile ||
                ordinaryAppHostArgumentCount == 0)
            {
                includeProfileArgumentsInSpec =
                    mechanism == ExecutableLaunchMechanism.Process ||
                    !projectLaunchConfigurationHandlesLaunchProfile;

                projectLaunchProfileArguments = GetLaunchProfileArguments(resource.GetEffectiveLaunchProfile()?.LaunchProfile);
                if (projectLaunchProfileArguments.Count > 0 &&
                    ordinaryAppHostArgumentCount > 0 &&
                    launchToolArgumentCount == 0 &&
                    HasDotnetApplicationArgumentBoundary() &&
                    dotnetProjectApplicationArgumentBoundaryIndex < 0)
                {
                    // A generated or explicit `dotnet run`/`dotnet watch` invocation needs `--` before application
                    // arguments. Custom IDE launchers consume raw application arguments and do not.
                    projectLaunchProfileArguments.Insert(0, "--");
                }
            }

            bool HasDotnetApplicationArgumentBoundary()
            {
                if (executableArgumentStartIndex > 0)
                {
                    return true;
                }

                return dotnetProjectLaunchResourceArgumentIndex is { } index && index >= omittedLaunchToolArgumentCount;
            }
        }

        var projectLaunchProfileArgumentInsertIndex =
            dotnetProjectLaunchResourceArgumentIndex is { } projectLaunchResourceArgumentIndex &&
            projectLaunchResourceArgumentIndex >= omittedLaunchToolArgumentCount
                ? dotnetProjectApplicationArgumentBoundaryIndex >= 0
                    ? dotnetProjectApplicationArgumentBoundaryIndex + 1
                    : appHostArgumentList.Count
                : launchToolArgumentCount > 0
                    ? Math.Min(launchToolArgumentCount, appHostArgumentList.Count)
                    : 0;

        for (var i = 0; i <= appHostArgumentList.Count; i++)
        {
            if (i == projectLaunchProfileArgumentInsertIndex && projectLaunchProfileArguments is not null)
            {
                launchArguments.AddRange(projectLaunchProfileArguments.Select(argument => CreateLaunchArgument(
                    argument,
                    isSensitive: false,
                    executable: includeProfileArgumentsInSpec,
                    display: true,
                    role: ExecutableLaunchArgumentRole.LaunchProfile)));
            }

            if (i == appHostArgumentList.Count)
            {
                break;
            }

            var argument = appHostArgumentList[i];
            var isLaunchToolArgument = i < launchToolArgumentCount;
            var launchArgument = CreateLaunchArgument(
                argument.Value,
                argument.IsSensitive,
                executable: i >= omittedLaunchToolArgumentCount,
                display: showLaunchToolArgumentsInCommandLine || !isLaunchToolArgument,
                role: isLaunchToolArgument ? ExecutableLaunchArgumentRole.LaunchTool : ExecutableLaunchArgumentRole.Application);
            if (dotnetProjectLaunchResourceArgumentIndex == i && launchArgument.Executable)
            {
                dotnetProjectLaunchArgumentIndex = launchArguments.Count;
            }
            launchArguments.Add(launchArgument);
        }

        return (launchArguments, dotnetProjectLaunchArgumentIndex);
    }

    private static int? FindExecutableAnnotatedDotnetProjectLaunchArgument(
        IResource resource,
        IReadOnlyList<(string Value, bool IsSensitive)> appHostArguments)
    {
        if (!IsExecutableAnnotatedDotnetProject(resource))
        {
            return null;
        }

        // Parse only the supported non-terminating prefix forms:
        //   dotnet run ...
        //   dotnet [env:NAME=value] --diagnostics run ...
        //   dotnet -d watch ...
        // Opaque response files, runtime options, nested commands, and application paths stop recognition because
        // a later `run` or `watch` token would not be the top-level project command.
        // See https://learn.microsoft.com/dotnet/core/tools/dotnet and
        // https://github.com/dotnet/command-line-api/blob/main/src/System.CommandLine/EnvironmentVariablesDirective.cs.
        var projectLaunchArgumentIndex = 0;
        var hasEnvironmentVariableDirective = false;
        while (projectLaunchArgumentIndex < appHostArguments.Count &&
            IsDotnetEnvironmentVariableDirective(appHostArguments[projectLaunchArgumentIndex].Value))
        {
            hasEnvironmentVariableDirective = true;
            projectLaunchArgumentIndex++;
        }

        while (projectLaunchArgumentIndex < appHostArguments.Count &&
            IsDotnetSdkDiagnosticOption(appHostArguments[projectLaunchArgumentIndex].Value))
        {
            projectLaunchArgumentIndex++;
        }

        if (projectLaunchArgumentIndex >= appHostArguments.Count)
        {
            return null;
        }

        return appHostArguments[projectLaunchArgumentIndex].Value switch
        {
            "run" => projectLaunchArgumentIndex,
            // .NET 10 cannot resolve the external watch command through an environment directive.
            "watch" when !hasEnvironmentVariableDirective => projectLaunchArgumentIndex,
            _ => null
        };
    }

    private static bool IsDotnetEnvironmentVariableDirective(string argument) =>
        string.Equals(argument, "[env]", StringComparison.OrdinalIgnoreCase) ||
        argument.StartsWith("[env:", StringComparison.OrdinalIgnoreCase) && argument.EndsWith(']');

    private static bool IsDotnetSdkDiagnosticOption(string argument) =>
        argument is "-d" or "--diagnostics";

    private static bool IsExecutableAnnotatedDotnetProject(IResource resource) =>
        resource is ProjectResource &&
        resource.TryGetLastAnnotation<ExecutableAnnotation>(out var executableAnnotation) &&
        string.Equals(Path.GetFileNameWithoutExtension(executableAnnotation.Command), "dotnet", StringComparison.OrdinalIgnoreCase);

    private static void AddDotnetProjectLaunchArguments(
        List<ExecutableLaunchArgument> launchArguments,
        int? dotnetProjectLaunchArgumentIndex,
        int executableArgumentStartIndex,
        string? appHostConfiguration)
    {
        if (dotnetProjectLaunchArgumentIndex is not { } projectLaunchIndex)
        {
            return;
        }

        var argumentsToInsert = new List<string>();
        if (!string.IsNullOrEmpty(appHostConfiguration) &&
            !ContainsDotnetProjectLaunchOption(launchArguments, "--configuration", "-c"))
        {
            argumentsToInsert.AddRange(["--configuration", appHostConfiguration]);
        }

        if (!ContainsDotnetProjectLaunchOption(launchArguments, "--no-launch-profile") &&
            !ContainsDotnetProjectLaunchOption(launchArguments, "--launch-profile"))
        {
            argumentsToInsert.Add("--no-launch-profile");
        }

        if (argumentsToInsert.Count == 0)
        {
            return;
        }

        launchArguments.InsertRange(
            projectLaunchIndex + 1,
            argumentsToInsert.Select(argument => new ExecutableLaunchArgument(
                argument,
                isSensitive: false,
                executable: true,
                display: false,
                effectiveArgumentIndex: null,
                role: ExecutableLaunchArgumentRole.ToolOption)));
        ReindexExecutableLaunchArguments(launchArguments, executableArgumentStartIndex);
    }

    private static bool ContainsDotnetProjectLaunchOption(
        List<ExecutableLaunchArgument> launchArguments,
        params string[] options)
    {
        var separatorIndex = launchArguments.FindIndex(argument =>
            argument.Executable &&
            string.Equals(argument.Value, "--", StringComparison.Ordinal));
        var endIndex = separatorIndex < 0 ? launchArguments.Count : separatorIndex;

        for (var i = 0; i < endIndex; i++)
        {
            var value = launchArguments[i].Value;
            if (options.Any(option =>
                string.Equals(value, option, StringComparison.Ordinal) ||
                value.StartsWith(option + "=", StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    private static void ReindexExecutableLaunchArguments(
        List<ExecutableLaunchArgument> launchArguments,
        int executableArgumentStartIndex)
    {
        var nextExecutableArgumentIndex = executableArgumentStartIndex;
        for (var i = 0; i < launchArguments.Count; i++)
        {
            var argument = launchArguments[i];
            launchArguments[i] = argument.WithEffectiveArgumentIndex(
                argument.Executable ? nextExecutableArgumentIndex++ : null);
        }
    }

    private static List<string> GetLaunchProfileArguments(LaunchProfile? launchProfile) =>
        launchProfile is not null && !string.IsNullOrWhiteSpace(launchProfile.CommandLineArgs)
            ? CommandLineArgsParser.Parse(launchProfile.CommandLineArgs)
            : [];
}
