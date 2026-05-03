// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

#pragma warning disable ASPIREDOTNETTOOL
#pragma warning disable ASPIREINTERACTION001 // InteractionInput is used to exercise resource command arguments.

var builder = DistributedApplication.CreateBuilder(args);
builder.Services.AddHttpClient();
builder.Services.AddHealthChecks().AddAsyncCheck("health-test", async (ct) =>
{
    await Task.Delay(5_000, ct);
    return HealthCheckResult.Healthy();
});

var manualArgSource = builder.AddExecutable("manual-arg-source", "dotnet", Environment.CurrentDirectory)
    .WithHttpEndpoint(targetPort: 8088);
var manualArgEndpoint = manualArgSource.GetEndpoint("http");

manualArgSource.WithArgs("--dashboard-port")
    .WithArgs(c => c.Args.Add(manualArgEndpoint.Property(EndpointProperty.Port)));

builder.AddProject<Projects.Stress_Empty>("manual-project-args", launchProfileName: null)
    .WithArgs("--dashboard-port")
    .WithArgs(c => c.Args.Add(manualArgEndpoint.Property(EndpointProperty.Port)));

builder.AddContainer("manual-container-args", "alpine")
    .WithEntrypoint("sleep")
    .WithArgs("3600", "--dashboard-port")
    .WithArgs(c => c.Args.Add(manualArgEndpoint.Property(EndpointProperty.Port)));

builder.AddDotnetTool("manual-dotnet-tool-args", "dotnet-dump")
    .WithArgs("--version", "--dashboard-port")
    .WithArgs(c => c.Args.Add(manualArgEndpoint.Property(EndpointProperty.Port)))
    .WithExplicitStart();

for (var i = 0; i < 2; i++)
{
    var name = $"test-{i:0000}";
    var rb = builder.AddTestResource(name);
    IResource parent = rb.Resource;

    for (var j = 0; j < 3; j++)
    {
        name += $"-n{j}";
        var nestedRb = builder.AddNestedResource(name, parent);
        parent = nestedRb.Resource;
    }
}

builder.AddParameter("testParameterResource", () => "value", secret: true);
var apiKeyParam = builder.AddParameter("api-key", secret: true);
var connectionStringParam = builder.AddParameter("db-connection-string");
builder.AddContainer("hiddenContainer", "alpine")
    .WithInitialState(new CustomResourceSnapshot
    {
        ResourceType = "CustomHiddenContainerType",
        Properties = [],
        IsHidden = true
    });

// TODO: OTEL env var can be removed when OTEL libraries are updated to 1.9.0
// See https://github.com/open-telemetry/opentelemetry-dotnet/blob/main/RELEASENOTES.md#1100
var serviceBuilder = builder.AddProject<Projects.Stress_ApiService>("stress-apiservice", launchProfileName: null)
    .WithEnvironment("OTEL_DOTNET_EXPERIMENTAL_METRICS_EMIT_OVERFLOW_ATTRIBUTE", "true")
    .WithEnvironment("API_KEY", apiKeyParam)
    .WithEnvironment("DB_CONNECTION_STRING", connectionStringParam)
    .WithIconName("Server");
serviceBuilder
    .WithEnvironment("HOST", $"{serviceBuilder.GetEndpoint("http").Property(EndpointProperty.Host)}")
    .WithEnvironment("PORT", $"{serviceBuilder.GetEndpoint("http").Property(EndpointProperty.Port)}")
    .WithEnvironment("URL", $"{serviceBuilder.GetEndpoint("http").Property(EndpointProperty.Url)}");
serviceBuilder.WithCommand(
    name: "icon-test",
    displayName: "Icon test",
    executeCommand: (c) =>
    {
        return Task.FromResult(CommandResults.Success());
    },
    commandOptions: new CommandOptions
    {
        IconName = "CloudDatabase"
    });
serviceBuilder.WithCommand(
    name: "icon-test-highlighted",
    displayName: "Icon test highlighted",
    executeCommand: (c) =>
    {
        return Task.FromResult(CommandResults.Success());
    },
    commandOptions: new CommandOptions
    {
        IconName = "CloudDatabase",
        IsHighlighted = true
    });
serviceBuilder.WithCommand(
    name: "echo-command-arguments",
    displayName: "Echo command arguments",
    executeCommand: (c) =>
    {
        if (c.Arguments is null)
        {
            return Task.FromResult(CommandResults.Failure("No command arguments were supplied."));
        }

        var arguments = c.Arguments.Value;
        var response = new
        {
            Message = arguments.GetProperty("message").GetString(),
            Count = arguments.GetProperty("count").GetDouble(),
            Urgent = arguments.TryGetProperty("urgent", out var urgent) && urgent.GetBoolean()
        };

        return Task.FromResult(CreateJsonSuccess("Echoed command arguments.", response));
    },
    commandOptions: new CommandOptions
    {
        Description = "Common API-only command that accepts required text and number arguments plus an optional boolean.",
        IconName = "Send",
        Visibility = ResourceCommandVisibility.Api,
        ArgumentInputs =
        [
            new InteractionInput
            {
                Name = "message",
                Label = "Message",
                InputType = InputType.Text,
                Required = true,
                Placeholder = "Hello"
            },
            new InteractionInput
            {
                Name = "count",
                Label = "Count",
                InputType = InputType.Number,
                Required = true
            },
            new InteractionInput
            {
                Name = "urgent",
                Label = "Urgent",
                InputType = InputType.Boolean
            }
        ]
    });
serviceBuilder.WithCommand(
    name: "echo-arguments",
    displayName: "Echo arguments",
    executeCommand: c =>
    {
        var arguments = ReadCommandArguments<EchoCommandArguments>(c.Arguments);
        if (string.IsNullOrWhiteSpace(arguments.Message))
        {
            return Task.FromResult(CommandResults.Failure("The message argument is required."));
        }
        if (arguments.Repeat is < 1 or > 10)
        {
            return Task.FromResult(CommandResults.Failure("The repeat argument must be between 1 and 10."));
        }

        var message = arguments.Shout ? arguments.Message.ToUpperInvariant() : arguments.Message;
        var payload = new
        {
            arguments.Message,
            arguments.Repeat,
            arguments.Shout,
            arguments.Flavor,
            SecretLength = arguments.Secret?.Length ?? 0,
            Echoed = Enumerable.Repeat(message, arguments.Repeat).ToArray()
        };

        return Task.FromResult(CreateJsonSuccess("Echoed command arguments.", payload, displayImmediately: true));
    },
    commandOptions: new CommandOptions
    {
        Description = "Common dashboard/API command with text, number, boolean, choice, and secret argument inputs.",
        IconName = "Code",
        ArgumentInputs =
        [
            new InteractionInput
            {
                Name = "message",
                Label = "Message",
                Description = "Text value to echo.",
                InputType = InputType.Text,
                Required = true,
                Placeholder = "Hello from the Stress playground",
                MaxLength = 80
            },
            new InteractionInput
            {
                Name = "repeat",
                Label = "Repeat",
                Description = "How many times to echo the message.",
                InputType = InputType.Number,
                Value = "1"
            },
            new InteractionInput
            {
                Name = "shout",
                Label = "Shout",
                Description = "Uppercase the echoed message.",
                InputType = InputType.Boolean,
                Value = "false"
            },
            new InteractionInput
            {
                Name = "flavor",
                Label = "Flavor",
                Description = "Choice argument used to verify select input rendering.",
                InputType = InputType.Choice,
                Value = "vanilla",
                Options =
                [
                    KeyValuePair.Create("vanilla", "Vanilla"),
                    KeyValuePair.Create("chocolate", "Chocolate"),
                    KeyValuePair.Create("strawberry", "Strawberry")
                ]
            },
            new InteractionInput
            {
                Name = "secret",
                Label = "Secret",
                Description = "Secret text input. The command only returns its length.",
                InputType = InputType.SecretText,
                Required = false,
                Placeholder = "Optional secret"
            }
        ]
    });
serviceBuilder.WithCommand(
    name: "validate-arguments",
    displayName: "Validate arguments",
    executeCommand: c =>
    {
        var arguments = ReadCommandArguments<ValidateCommandArguments>(c.Arguments);
        if (string.IsNullOrWhiteSpace(arguments.Target))
        {
            return Task.FromResult(CommandResults.Failure("The target argument is required."));
        }
        if (arguments.TimeoutSeconds <= 0)
        {
            return Task.FromResult(CommandResults.Failure("The timeoutSeconds argument must be positive."));
        }

        var payload = new
        {
            arguments.Target,
            arguments.Mode,
            arguments.TimeoutSeconds,
            arguments.RequireHealthy,
            ValidatedAt = DateTimeOffset.UtcNow
        };

        return arguments.Mode == "fail"
            ? Task.FromResult(CreateJsonFailure("Argument validation failed.", payload))
            : Task.FromResult(CreateJsonSuccess("Argument validation passed.", payload));
    },
    commandOptions: new CommandOptions
    {
        Description = "Less common validation command with failure results, required inputs, defaults, and choice validation.",
        IconName = "CheckmarkCircle",
        ArgumentInputs =
        [
            new InteractionInput
            {
                Name = "target",
                Label = "Target",
                Description = "Target resource or subsystem to validate.",
                InputType = InputType.Text,
                Required = true,
                Placeholder = "stress-apiservice",
                MaxLength = 80
            },
            new InteractionInput
            {
                Name = "mode",
                Label = "Mode",
                Description = "Select success to pass, fail to return a structured failure, or dry-run for a successful validation preview.",
                InputType = InputType.Choice,
                Value = "success",
                Options =
                [
                    KeyValuePair.Create("success", "Success"),
                    KeyValuePair.Create("fail", "Fail"),
                    KeyValuePair.Create("dry-run", "Dry run")
                ]
            },
            new InteractionInput
            {
                Name = "timeoutSeconds",
                Label = "Timeout seconds",
                Description = "Positive timeout value to validate.",
                InputType = InputType.Number,
                Value = "30"
            },
            new InteractionInput
            {
                Name = "requireHealthy",
                Label = "Require healthy",
                Description = "Boolean argument used to verify checkbox input rendering.",
                InputType = InputType.Boolean,
                Value = "true"
            }
        ]
    });
serviceBuilder.WithCommand(
    name: "argument-stress-test",
    displayName: "Argument stress test",
    executeCommand: c =>
    {
        if (c.Arguments is null)
        {
            return Task.FromResult(CommandResults.Failure("No command arguments were supplied."));
        }

        var properties = c.Arguments.Value.EnumerateObject().ToArray();
        var payload = new
        {
            PropertyCount = properties.Length,
            StringCharacters = properties
                .Where(p => p.Value.ValueKind == JsonValueKind.String)
                .Sum(p => p.Value.GetString()?.Length ?? 0),
            NumberCount = properties.Count(p => p.Value.ValueKind == JsonValueKind.Number),
            BooleanCount = properties.Count(p => p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False),
            PropertyNames = properties.Select(p => p.Name).ToArray()
        };

        return Task.FromResult(CreateJsonSuccess("Summarized command arguments.", payload, displayImmediately: true));
    },
    commandOptions: new CommandOptions
    {
        Description = "Minor dashboard/API stress command with many dynamically generated inputs to exercise argument metadata and payload handling.",
        IconName = "TableLightning",
        Visibility = ResourceCommandVisibility.Dashboard | ResourceCommandVisibility.Api,
        ArgumentInputs = CreateArgumentStressInputs(fieldCount: 20)
    });

serviceBuilder.WithHttpEndpoint(5180, name: $"http");
for (var i = 1; i <= 30; i++)
{
    var port = 5180 + i;
    serviceBuilder.WithHttpEndpoint(port, name: $"http-{port}");
}

serviceBuilder.WithHttpCommand("/write-console", "Write to console", commandOptions: new() { Method = HttpMethod.Get, IconName = "ContentViewGalleryLightning" });
serviceBuilder.WithHttpCommand("/write-console-large", "Write to console large", commandOptions: new() { Method = HttpMethod.Get, IconName = "ContentViewGalleryLightning" });
serviceBuilder.WithHttpCommand("/increment-counter", "Increment counter", commandOptions: new() { Method = HttpMethod.Get, IconName = "ContentViewGalleryLightning" });
serviceBuilder.WithHttpCommand("/big-trace", "Big trace", commandOptions: new() { Method = HttpMethod.Get, IconName = "ContentViewGalleryLightning" });
serviceBuilder.WithHttpCommand("/trace-limit", "Trace limit", commandOptions: new() { Method = HttpMethod.Get, IconName = "ContentViewGalleryLightning" });
serviceBuilder.WithHttpCommand("/log-message", "Log message", commandOptions: new() { Method = HttpMethod.Get, IconName = "ContentViewGalleryLightning" });
serviceBuilder.WithHttpCommand("/log-message-limit", "Log message limit", commandOptions: new() { Method = HttpMethod.Get, IconName = "ContentViewGalleryLightning" });
serviceBuilder.WithHttpCommand("/log-message-limit-large", "Log message limit large", commandOptions: new() { Method = HttpMethod.Get, IconName = "ContentViewGalleryLightning" });
serviceBuilder.WithHttpCommand("/http-command-auto-result", "HTTP command auto result", commandOptions: new() { Method = HttpMethod.Get, IconName = "ContentViewGalleryLightning", ResultMode = HttpCommandResultMode.Auto, Description = "Run an HTTP command and infer the result format from the response content type" });
serviceBuilder.WithHttpCommand("/http-command-json-result", "HTTP command JSON result", commandOptions: new() { Method = HttpMethod.Get, IconName = "ContentViewGalleryLightning", ResultMode = HttpCommandResultMode.Json, Description = "Run an HTTP command and flow the JSON response back to the caller" });
serviceBuilder.WithHttpCommand("/http-command-text-result", "HTTP command text result", commandOptions: new() { Method = HttpMethod.Get, IconName = "ContentViewGalleryLightning", ResultMode = HttpCommandResultMode.Text, Description = "Run an HTTP command and flow the plain-text response back to the caller" });
serviceBuilder.WithHttpCommand("/multiple-traces-linked", "Multiple traces linked", commandOptions: new() { Method = HttpMethod.Get, IconName = "ContentViewGalleryLightning" });
serviceBuilder.WithHttpCommand("/overflow-counter", "Overflow counter", commandOptions: new() { Method = HttpMethod.Get, IconName = "ContentViewGalleryLightning" });
serviceBuilder.WithHttpCommand("/nested-trace-spans", "Out of order nested spans", commandOptions: new() { Method = HttpMethod.Get, IconName = "ContentViewGalleryLightning" });
serviceBuilder.WithHttpCommand("/exemplars-no-span", "Examplars with no span", commandOptions: new() { Method = HttpMethod.Get, IconName = "ContentViewGalleryLightning" });
serviceBuilder.WithHttpCommand("/genai-trace", "Gen AI trace", commandOptions: new() { Method = HttpMethod.Get, IconName = "ContentViewGalleryLightning" });
serviceBuilder.WithHttpCommand("/genai-langchain-trace", "Gen AI LangChain trace", commandOptions: new() { Method = HttpMethod.Get, IconName = "ContentViewGalleryLightning" });
serviceBuilder.WithHttpCommand("/genai-trace-display-error", "Gen AI trace display error", commandOptions: new() { Method = HttpMethod.Get, IconName = "ContentViewGalleryLightning" });
serviceBuilder.WithHttpCommand("/genai-evaluations", "Gen AI evaluations", commandOptions: new() { Method = HttpMethod.Get, IconName = "ContentViewGalleryLightning" });
serviceBuilder.WithHttpCommand("/log-formatting", "Log formatting", commandOptions: new() { Method = HttpMethod.Get, IconName = "ContentViewGalleryLightning" });
serviceBuilder.WithHttpCommand("/big-nested-trace", "Big nested trace", commandOptions: new() { Method = HttpMethod.Get, IconName = "ContentViewGalleryLightning" });

builder.AddProject<Projects.Stress_TelemetryService>("stress-telemetryservice")
       .WithUrls(c => c.Urls.Add(new() { Url = "https://someplace.com", DisplayText = "Some place" }))
       .WithUrl("https://someotherplace.com/some-path", "Some other place")
       .WithUrl("https://extremely-long-url.com/abcdefghijklmnopqrstuvwxyz/abcdefghijklmnopqrstuvwxyz/abcdefghijklmnopqrstuvwxyz//abcdefghijklmnopqrstuvwxyz/abcdefghijklmnopqrstuvwxyz/abcdefghijklmnopqrstuvwxyz/abcdefghijklmnopqrstuvwxyz/abcdefghijklmno")
       .AddInteractionCommands()
       .WithCommand(
           name: "long-command",
           displayName: "This is a custom command with a very long command display name",
           executeCommand: (c) =>
           {
               return Task.FromResult(CommandResults.Success());
           },
           commandOptions: new() { IconName = "CloudDatabase" })
       .WithCommand(
           name: "resource-stop-all",
           displayName: "Stop all resources",
           executeCommand: async (c) =>
           {
               await ExecuteCommandForAllResourcesAsync(c.ServiceProvider, KnownResourceCommands.StopCommand, c.CancellationToken);
               return CommandResults.Success();
           },
           commandOptions: new() { IconName = "Stop", IconVariant = IconVariant.Filled })
       .WithCommand(
           name: "resource-start-all",
           displayName: "Start all resources",
           executeCommand: async (c) =>
           {
               await ExecuteCommandForAllResourcesAsync(c.ServiceProvider, KnownResourceCommands.StartCommand, c.CancellationToken);
               return CommandResults.Success();
           },
           commandOptions: new() { IconName = "Play", IconVariant = IconVariant.Filled })
       .WithCommand(
           name: "generate-token",
           displayName: "Generate Token",
           executeCommand: (c) =>
           {
               var token = new
               {
                   accessToken = Convert.ToBase64String(Guid.NewGuid().ToByteArray()) + Convert.ToBase64String(Guid.NewGuid().ToByteArray()),
                   tokenType = "Bearer",
                   expiresIn = 3600,
                   scope = "api.read api.write",
                   issuedAt = DateTime.UtcNow
               };
               var json = JsonSerializer.Serialize(token, new JsonSerializerOptions { WriteIndented = true });
               var resultData = new CommandResultData
               {
                   Value = json,
                   Format = CommandResultFormat.Json
               };
               return Task.FromResult(CommandResults.Success("Generated token.", resultData));
           },
           commandOptions: new() { IconName = "Key", Description = "Generate a temporary access token" })
       .WithCommand(
           name: "get-connection-string",
           displayName: "Get Connection String",
           executeCommand: (c) =>
           {
               var connectionString = $"Server=localhost,1433;Database=StressDb;User Id=sa;Password={Guid.NewGuid():N};TrustServerCertificate=true";
               var message = """
                   Retrieved connection string. The database connection was established successfully
                   after verifying TLS certificates and negotiating encryption parameters.

                   The server responded with protocol version 7.4 and confirmed support for multiple
                   active result sets. Connection pooling is enabled with a maximum pool size of 100
                   connections and a minimum of 10 idle connections maintained.

                   The login handshake completed in 42ms with SSPI authentication. All pre-login
                   checks passed including network library validation and instance name resolution.
                   """;
               return Task.FromResult(CommandResults.Success(message, new CommandResultData { Value = connectionString, DisplayImmediately = true }));
           },
           commandOptions: new() { IconName = "LinkMultiple", Description = "Get the connection string for this resource" })
       .WithCommand(
           name: "validate-config",
           displayName: "Validate Config",
           executeCommand: (c) =>
           {
               var errors = new { errors = new[] { new { field = "connectionString", message = "Invalid host" }, new { field = "timeout", message = "Must be positive" } } };
               var json = JsonSerializer.Serialize(errors, new JsonSerializerOptions { WriteIndented = true });
               return Task.FromResult(CommandResults.Failure("Validation failed", json, CommandResultFormat.Json));
           },
           commandOptions: new() { IconName = "Warning", Description = "Validate resource configuration (always fails with details)" })
       .WithCommand(
           name: "check-health",
           displayName: "Check Health",
           executeCommand: (c) =>
           {
               return Task.FromResult(CommandResults.Failure("Health check failed", "Connection refused: ECONNREFUSED 127.0.0.1:5432\nRetries exhausted after 3 attempts", CommandResultFormat.Text));
           },
           commandOptions: new() { IconName = "HeartBroken", Description = "Check resource health (always fails with details)" })
       .WithCommand(
           name: "migrate-database",
           displayName: "Migrate Database",
           executeCommand: (c) =>
           {
               var markdown = """
                   # ⚙️ Database Migration Summary

                   | Table      | Result                     |
                   |------------|----------------------------|
                   | Customers  | ✅ 1,200 rows              |
                   | Products   | ✅ 850 rows                |
                   | Orders     | ✅ 3,400 rows              |
                   | OrderItems | ✅ 8,750 rows              |
                   | Categories | ✅ 45 rows                 |
                   | Reviews    | ❌ FK constraint violation |
                   | Inventory  | ✅ 850 rows                |
                   | Shipping   | ✅ 3,400 rows              |
                   | Payments   | ❌ Timeout after 30s       |
                   | Coupons    | ✅ 120 rows                |

                   **Summary:** 8 of 10 tables migrated successfully. 2 tables failed.
                   """;
               return Task.FromResult(CommandResults.Success("Database migrated.", new CommandResultData { Value = markdown, Format = CommandResultFormat.Markdown }));
           },
           commandOptions: new() { IconName = "CloudDatabase", Description = "Migrate the database with sample store data" });

#if !SKIP_DASHBOARD_REFERENCE
// This project is only added in playground projects to support development/debugging
// of the dashboard. It is not required in end developer code. Comment out this code
// or build with `/p:SkipDashboardReference=true`, to test end developer
// dashboard launch experience, Refer to Directory.Build.props for the path to
// the dashboard binary (defaults to the Aspire.Dashboard bin output in the
// artifacts dir).
builder.AddProject<Projects.Aspire_Dashboard>(KnownResourceNames.AspireDashboard);
#endif

builder.AddExecutable("executableWithSingleArg", "dotnet", Environment.CurrentDirectory, "--version");
builder.AddExecutable("executableWithSingleEscapedArg", "dotnet", Environment.CurrentDirectory, "one two");
builder.AddExecutable("executableWithMultipleArgs", "dotnet", Environment.CurrentDirectory, "--version", "one two");

IResourceBuilder<IResource>? previousResourceBuilder = null;

for (var i = 0; i < 3; i++)
{
    var resourceBuilder = builder.AddProject<Projects.Stress_Empty>($"empty-{i:0000}", launchProfileName: null)
                                .WithIconName("Document");
    if (previousResourceBuilder != null)
    {
        resourceBuilder.WaitFor(previousResourceBuilder);
        resourceBuilder.WithHealthCheck("health-test");
    }

    previousResourceBuilder = resourceBuilder;
}

builder.AddProject<Projects.Stress_Empty>("empty-profile-1", launchProfileName: "Profile1");
builder.AddProject<Projects.Stress_Empty>("empty-profile-2", launchProfileName: "Profile1")
    .WithEnvironment("APPHOST_ENV_VAR", "test")
    .WithEnvironment("ENV_TO_OVERRIDE", "this value came from the apphost")
    .WithArgs("arg_from_apphost");

builder.Build().Run();

static ExecuteCommandResult CreateJsonSuccess(string message, object payload, bool displayImmediately = false)
{
    return CommandResults.Success(message, new CommandResultData
    {
        Value = SerializeCommandPayload(payload),
        Format = CommandResultFormat.Json,
        DisplayImmediately = displayImmediately
    });
}

static ExecuteCommandResult CreateJsonFailure(string message, object payload)
{
    return CommandResults.Failure(message, SerializeCommandPayload(payload), CommandResultFormat.Json);
}

static string SerializeCommandPayload(object payload)
{
    return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
}

static T ReadCommandArguments<T>(JsonElement? arguments) where T : new()
{
    return arguments is null
        ? new T()
        : JsonSerializer.Deserialize<T>(arguments.Value.GetRawText(), new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? new T();
}

static IReadOnlyList<InteractionInput> CreateArgumentStressInputs(int fieldCount)
{
    var inputs = new List<InteractionInput>
    {
        new()
        {
            Name = "runId",
            Label = "Run ID",
            Description = "Identifier for this stress command invocation.",
            InputType = InputType.Text,
            Required = true,
            Placeholder = "stress-001",
            MaxLength = 64
        },
        new()
        {
            Name = "iterations",
            Label = "Iterations",
            Description = "Number input used to verify numeric payload handling.",
            InputType = InputType.Number,
            Required = true,
            Value = "5"
        },
        new()
        {
            Name = "enabled",
            Label = "Enabled",
            Description = "Boolean input used to verify boolean payload handling.",
            InputType = InputType.Boolean,
            Required = true,
            Value = "true"
        }
    };

    for (var i = 1; i <= fieldCount; i++)
    {
        inputs.Add(new()
        {
            Name = $"item{i:00}",
            Label = $"Item {i:00}",
            Description = "Generated text input used by the command argument stress test.",
            InputType = InputType.Text,
            Required = i <= 3,
            Placeholder = $"value-{i:00}",
            MaxLength = 64
        });
    }

    return inputs;
}

static async Task ExecuteCommandForAllResourcesAsync(IServiceProvider serviceProvider, string commandName, CancellationToken cancellationToken)
{
    var commandService = serviceProvider.GetRequiredService<ResourceCommandService>();
    var model = serviceProvider.GetRequiredService<DistributedApplicationModel>();

    var resources = model.Resources
        .Where(r => r.IsContainer() || r is ProjectResource || r is ExecutableResource)
        .Where(r => r.Name != KnownResourceNames.AspireDashboard)
        .ToList();

    var commandTasks = new List<Task>();
    foreach (var r in resources)
    {
        commandTasks.Add(commandService.ExecuteCommandAsync(r, commandName, cancellationToken));
    }
    await Task.WhenAll(commandTasks).ConfigureAwait(false);
}

file sealed class EchoCommandArguments
{
    public string? Message { get; set; }

    public int Repeat { get; set; } = 1;

    public bool Shout { get; set; }

    public string Flavor { get; set; } = "vanilla";

    public string? Secret { get; set; }
}

file sealed class ValidateCommandArguments
{
    public string? Target { get; set; }

    public string Mode { get; set; } = "success";

    public int TimeoutSeconds { get; set; } = 30;

    public bool RequireHealthy { get; set; } = true;
}
