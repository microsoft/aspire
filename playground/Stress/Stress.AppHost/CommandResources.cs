// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

#pragma warning disable ASPIREPROCESSCOMMAND001 // Process command APIs are experimental.

internal static class CommandResources
{
    private static string NodeExecutablePath { get; } = Environment.GetEnvironmentVariable("NODE") ?? "node";
    private static string DotnetExecutablePath { get; } = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";

    public static void AddCommandResources(
        this IDistributedApplicationBuilder builder,
        IResourceBuilder<ProjectResource> serviceBuilder,
        IResourceBuilder<ProjectResource> telemetryBuilder)
    {
        AddServiceCommands(builder, serviceBuilder);
        AddTelemetryCommands(builder, telemetryBuilder);
    }

    private static void AddServiceCommands(IDistributedApplicationBuilder builder, IResourceBuilder<ProjectResource> serviceBuilder)
    {
        var iconCommands = builder.AddCommandGroup("icon-commands", serviceBuilder.Resource);
        iconCommands.WithCommand(
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
        iconCommands.WithCommand(
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
        // Commands with unknown/missing icons to stress test issue #18385.
        iconCommands.WithCommand(
            name: "unknown-icon",
            displayName: "Unknown icon",
            executeCommand: (c) =>
            {
                return Task.FromResult(CommandResults.Success());
            },
            commandOptions: new CommandOptions
            {
                IconName = "Bracket"
            });
        iconCommands.WithCommand(
            name: "unknown-icon-highlighted",
            displayName: "Simulate knockout — prediction phase",
            executeCommand: (c) =>
            {
                return Task.FromResult(CommandResults.Success());
            },
            commandOptions: new CommandOptions
            {
                IconName = "Bracket",
                IsHighlighted = true
            });
        iconCommands.WithCommand(
            name: "unknown-icon-highlighted-short",
            displayName: "Short",
            executeCommand: (c) =>
            {
                return Task.FromResult(CommandResults.Success());
            },
            commandOptions: new CommandOptions
            {
                IconName = "NotARealIconName",
                IsHighlighted = true
            });
        iconCommands.WithCommand(
            name: "no-icon",
            displayName: "No icon at all",
            executeCommand: (c) =>
            {
                return Task.FromResult(CommandResults.Success());
            },
            commandOptions: new CommandOptions());
        iconCommands.WithCommand(
            name: "no-icon-highlighted",
            displayName: "No icon highlighted with a very long display name to test overflow",
            executeCommand: (c) =>
            {
                return Task.FromResult(CommandResults.Success());
            },
            commandOptions: new CommandOptions
            {
                IsHighlighted = true
            });

        var argumentCommands = builder.AddCommandGroup("argument-commands", serviceBuilder.Resource);
        argumentCommands.WithCommand(
            name: "echo-command-arguments",
            displayName: "Echo command arguments",
            executeCommand: (c) =>
            {
                var arguments = c.Arguments;
                if (string.IsNullOrWhiteSpace(arguments.GetString("message")) || string.IsNullOrWhiteSpace(arguments.GetString("count")))
                {
                    return Task.FromResult(CommandResults.Failure("The message and count arguments are required."));
                }

                var response = new
                {
                    Message = arguments.GetString("message"),
                    Count = arguments.GetDouble("count"),
                    Urgent = arguments.GetString("urgent") is { } urgent && bool.Parse(urgent)
                };

                return Task.FromResult(CreateJsonSuccess("Echoed command arguments.", response));
            },
            commandOptions: new CommandOptions
            {
                Description = "Common API-only command that accepts required text and number arguments plus an optional boolean.",
                IconName = "Send",
                Visibility = ResourceCommandVisibility.Api,
                Arguments =
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
        argumentCommands.WithCommand(
            name: "echo-arguments",
            displayName: "Echo arguments",
            executeCommand: c =>
            {
                var arguments = ReadEchoCommandArguments(c.Arguments);
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
                Arguments =
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
        argumentCommands.WithCommand(
            name: "validate-arguments",
            displayName: "Validate arguments",
            executeCommand: c =>
            {
                var arguments = ReadValidateCommandArguments(c.Arguments);
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
                ValidateArguments = context =>
                {
                    var arguments = ReadValidateCommandArguments(context.Inputs);
                    if (arguments.TimeoutSeconds <= 0)
                    {
                        context.AddValidationError("timeoutSeconds", "The timeoutSeconds argument must be positive.");
                    }

                    return Task.CompletedTask;
                },
                Arguments =
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
        argumentCommands.WithCommand(
            name: "argument-stress-test",
            displayName: "Argument stress test",
            executeCommand: c =>
            {
                var arguments = c.Arguments;
                var inputsWithValues = arguments.Where(i => i.Value is not null).ToArray();
                var payload = new
                {
                    PropertyCount = inputsWithValues.Length,
                    StringCharacters = inputsWithValues
                        .Where(i => i.InputType is InputType.Text or InputType.SecretText or InputType.Choice)
                        .Sum(i => i.Value?.Length ?? 0),
                    NumberCount = inputsWithValues.Count(i => i.InputType == InputType.Number),
                    BooleanCount = inputsWithValues.Count(i => i.InputType == InputType.Boolean),
                    PropertyNames = inputsWithValues.Select(i => i.Name).ToArray()
                };

                return Task.FromResult(CreateJsonSuccess("Summarized command arguments.", payload, displayImmediately: true));
            },
            commandOptions: new CommandOptions
            {
                Description = "Minor dashboard/API stress command with many dynamically generated inputs to exercise argument metadata and payload handling.",
                IconName = "TableLightning",
                Visibility = ResourceCommandVisibility.UI | ResourceCommandVisibility.Api,
                Arguments = CreateArgumentStressInputs(fieldCount: 20)
            });
        argumentCommands.WithCommand(
            name: "dependent-arguments",
            displayName: "Dependent arguments",
            executeCommand: c =>
            {
                var arguments = c.Arguments;
                var payload = new
                {
                    Category = arguments.GetString("category"),
                    Item = arguments.GetString("item"),
                    Quantity = arguments.GetInt32("quantity"),
                    Priority = arguments.GetString("priority")
                };

                return Task.FromResult(CreateJsonSuccess("Dependent arguments received.", payload, displayImmediately: true));
            },
            commandOptions: new CommandOptions
            {
                Description = "Command with dependent inputs that dynamically load options based on other input values.",
                IconName = "BranchFork",
                Arguments =
                [
                    new InteractionInput
                    {
                        Name = "category",
                        Label = "Category",
                        Description = "Select a category to populate dependent items.",
                        InputType = InputType.Choice,
                        Required = true,
                        Placeholder = "Select category",
                        Options =
                        [
                            KeyValuePair.Create("fruit", "Fruit"),
                            KeyValuePair.Create("vegetable", "Vegetable"),
                            KeyValuePair.Create("dairy", "Dairy")
                        ]
                    },
                    new InteractionInput
                    {
                        Name = "item",
                        Label = "Item",
                        Description = "Items are loaded based on the selected category.",
                        InputType = InputType.Choice,
                        Required = true,
                        Placeholder = "Select item",
                        Disabled = true,
                        DynamicLoading = new InputLoadOptions
                        {
                            DependsOnInputs = ["category"],
                            LoadCallback = async (context) =>
                            {
                                await Task.Delay(1000, context.CancellationToken);

                                var category = context.AllInputs["category"].Value;
                                context.Input.Disabled = string.IsNullOrEmpty(category);
                                context.Input.Options = category switch
                                {
                                    "fruit" =>
                                    [
                                        KeyValuePair.Create("apple", "Apple"),
                                        KeyValuePair.Create("banana", "Banana"),
                                        KeyValuePair.Create("cherry", "Cherry")
                                    ],
                                    "vegetable" =>
                                    [
                                        KeyValuePair.Create("carrot", "Carrot"),
                                        KeyValuePair.Create("broccoli", "Broccoli"),
                                        KeyValuePair.Create("spinach", "Spinach")
                                    ],
                                    "dairy" =>
                                    [
                                        KeyValuePair.Create("milk", "Milk"),
                                        KeyValuePair.Create("cheese", "Cheese"),
                                        KeyValuePair.Create("yogurt", "Yogurt")
                                    ],
                                    _ => []
                                };
                            }
                        }
                    },
                    new InteractionInput
                    {
                        Name = "quantity",
                        Label = "Quantity",
                        Description = "Number of items to order.",
                        InputType = InputType.Number,
                        Required = true,
                        Value = "1"
                    },
                    new InteractionInput
                    {
                        Name = "priority",
                        Label = "Priority",
                        Description = "Priority is determined by the selected item.",
                        InputType = InputType.Choice,
                        Disabled = true,
                        Placeholder = "Determined by item",
                        DynamicLoading = new InputLoadOptions
                        {
                            DependsOnInputs = ["item"],
                            LoadCallback = async (context) =>
                            {
                                await Task.Delay(500, context.CancellationToken);

                                var item = context.AllInputs["item"].Value;
                                context.Input.Disabled = string.IsNullOrEmpty(item);

                                // Perishable items get fewer priority options.
                                var isPerishable = item is "banana" or "milk" or "spinach";
                                context.Input.Options = isPerishable
                                    ?
                                    [
                                        KeyValuePair.Create("express", "Express"),
                                        KeyValuePair.Create("overnight", "Overnight")
                                    ]
                                    :
                                    [
                                        KeyValuePair.Create("standard", "Standard"),
                                        KeyValuePair.Create("express", "Express"),
                                        KeyValuePair.Create("overnight", "Overnight")
                                    ];

                                if (string.IsNullOrEmpty(context.Input.Value))
                                {
                                    context.Input.Value = isPerishable ? "express" : "standard";
                                }
                            }
                        }
                    }
                ]
            });

        // These commands exercise local process-backed resource commands, including issue-inspired command shapes.
        var processCommands = builder.AddCommandGroup("process-commands", serviceBuilder.Resource);
        var processCommandScriptsDirectory = Path.Combine(builder.AppHostDirectory, "process-command-scripts");
        var processCommandStdinScriptPath = Path.Combine(processCommandScriptsDirectory, "stdin.js");
        var processCommandEnvironmentScriptPath = Path.Combine(processCommandScriptsDirectory, "environment.js");
        var processCommandWorkingDirectoryScriptPath = Path.Combine(processCommandScriptsDirectory, "working-directory.js");
        var processCommandStderrFailureScriptPath = Path.Combine(processCommandScriptsDirectory, "stderr-failure.js");
        var processCommandOutputLimitScriptPath = Path.Combine(processCommandScriptsDirectory, "output-limit.js");
        var processCommandBackupRestoreScriptPath = Path.Combine(processCommandScriptsDirectory, "backup-restore.js");
        var processCommandContainerExecScriptPath = Path.Combine(processCommandScriptsDirectory, "container-exec.js");
        var processCommandSampleBackupPath = Path.Combine(processCommandScriptsDirectory, "sample-backup.dump");
        var processCommandFileAppsDirectory = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "process-command-file-apps"));
        var processCommandCSharpArgumentsAndStdinAppPath = Path.Combine(processCommandFileAppsDirectory, "arguments-and-stdin.cs");
        var processCommandCSharpWorkingDirectoryFailureAppPath = Path.Combine(processCommandFileAppsDirectory, "working-directory-failure.cs");
        var processCommandIssueToolAppPath = Path.Combine(processCommandFileAppsDirectory, "issue-tool.cs");

        processCommands.WithProcessCommand(
            commandName: "dotnet-version",
            displayName: "Show .NET version",
            executablePath: "dotnet",
            arguments: ["--version"],
            commandOptions: new ProcessCommandOptions
            {
                Description = "Runs 'dotnet --version' from the AppHost and returns the process output.",
                IconName = "WindowConsole",
                MaxOutputLineCount = 10
            });
        processCommands.WithProcessCommand(
            commandName: "dotnet-command",
            displayName: "Run dotnet command",
            processSpecFactory: context =>
            {
                var command = context.Arguments.GetString("command") switch
                {
                    "info" => "--info",
                    _ => "--version"
                };

                return new ProcessCommandSpec("dotnet")
                {
                    Arguments = [command]
                };
            },
            commandOptions: new ProcessCommandOptions
            {
                Description = "Runs a selected dotnet command from the AppHost to exercise process command arguments.",
                IconName = "WindowConsole",
                MaxOutputLineCount = 25,
                Arguments =
                [
                    new InteractionInput
                    {
                        Name = "command",
                        Label = "Command",
                        Description = "Select which dotnet command to run.",
                        InputType = InputType.Choice,
                        Value = "version",
                        Options =
                        [
                            KeyValuePair.Create("version", "dotnet --version"),
                            KeyValuePair.Create("info", "dotnet --info")
                        ]
                    }
                ]
            });
        processCommands.WithProcessCommand(
            commandName: "process-stdin",
            displayName: "Process stdin",
            processSpecFactory: context => CreateNodeProcessSpec(
                processCommandStdinScriptPath,
                standardInputContent: context.Arguments.GetString("input") ?? "Hello from stdin"),
            commandOptions: new ProcessCommandOptions
            {
                Description = "Runs a process that reads one line from standard input.",
                IconName = "WindowConsole",
                MaxOutputLineCount = 10,
                Arguments =
                [
                    new InteractionInput
                    {
                        Name = "input",
                        Label = "Input",
                        Description = "Text written to the process standard input.",
                        InputType = InputType.Text,
                        Value = "Hello from stdin",
                        MaxLength = 100
                    }
                ]
            });
        processCommands.WithProcessCommand(
            commandName: "process-environment",
            displayName: "Process environment",
            processSpecFactory: _ => CreateNodeProcessSpec(
                processCommandEnvironmentScriptPath,
                environmentVariables: new Dictionary<string, string>
                {
                    ["PROCESS_COMMAND_SAMPLE"] = "from-process-command"
                },
                inheritEnvironmentVariables: false),
            commandOptions: new ProcessCommandOptions
            {
                Description = "Runs a process with a custom environment and environment inheritance disabled.",
                IconName = "WindowConsole",
                MaxOutputLineCount = 10
            });
        processCommands.WithProcessCommand(
            commandName: "process-working-directory",
            displayName: "Process working directory",
            processSpecFactory: _ => CreateNodeProcessSpec(
                processCommandWorkingDirectoryScriptPath,
                workingDirectory: builder.AppHostDirectory),
            commandOptions: new ProcessCommandOptions
            {
                Description = "Runs a process from the AppHost working directory and lists project files.",
                IconName = "Folder",
                MaxOutputLineCount = 10
            });
        processCommands.WithProcessCommand(
            commandName: "process-stderr-failure",
            displayName: "Process stderr failure",
            processSpecFactory: _ => CreateNodeProcessSpec(processCommandStderrFailureScriptPath),
            commandOptions: new ProcessCommandOptions
            {
                Description = "Runs a process that writes stdout/stderr and returns a non-zero exit code.",
                IconName = "Warning",
                MaxOutputLineCount = 10
            });
        processCommands.WithProcessCommand(
            commandName: "process-output-limit",
            displayName: "Process output limit",
            processSpecFactory: _ => CreateNodeProcessSpec(processCommandOutputLimitScriptPath),
            commandOptions: new ProcessCommandOptions
            {
                Description = "Runs a process that emits more lines than the command returns.",
                IconName = "TableLightning",
                MaxOutputLineCount = 5,
                DisplayImmediately = false
            });
        processCommands.WithProcessCommand(
            commandName: "process-csharp-file-app",
            displayName: "Process C# file app",
            processSpecFactory: context => CreateDotnetFileAppProcessSpec(
                processCommandCSharpArgumentsAndStdinAppPath,
                arguments: [context.Arguments.GetString("name") ?? "Aspire"],
                standardInputContent: context.Arguments.GetString("input") ?? "Hello from C#"),
            commandOptions: new ProcessCommandOptions
            {
                Description = "Runs a C# file-based app that reads command arguments and standard input.",
                IconName = "WindowConsole",
                MaxOutputLineCount = 10,
                Arguments =
                [
                    new InteractionInput
                    {
                        Name = "name",
                        Label = "Name",
                        Description = "Text passed as a C# file app command-line argument.",
                        InputType = InputType.Text,
                        Value = "Aspire",
                        MaxLength = 100
                    },
                    new InteractionInput
                    {
                        Name = "input",
                        Label = "Input",
                        Description = "Text written to the C# file app standard input.",
                        InputType = InputType.Text,
                        Value = "Hello from C#",
                        MaxLength = 100
                    }
                ]
            });
        processCommands.WithProcessCommand(
            commandName: "process-csharp-file-app-failure",
            displayName: "Process C# file app failure",
            processSpecFactory: _ => CreateDotnetFileAppProcessSpec(
                processCommandCSharpWorkingDirectoryFailureAppPath,
                workingDirectory: builder.AppHostDirectory),
            commandOptions: new ProcessCommandOptions
            {
                Description = "Runs a C# file-based app from the AppHost working directory that writes stdout/stderr and returns a non-zero exit code.",
                IconName = "Warning",
                MaxOutputLineCount = 10
            });
        processCommands.WithProcessCommand(
            commandName: "issue-seed-data",
            displayName: "Issue: Seed data",
            processSpecFactory: context => CreateDotnetFileAppProcessSpec(
                processCommandIssueToolAppPath,
                arguments:
                [
                    "seed",
                    context.Arguments.GetString("dataset") ?? "small",
                    context.Arguments.GetString("customers") ?? "25"
                ],
                environmentVariables: new Dictionary<string, string>
                {
                    ["ConnectionStrings__mainDb"] = "Host=localhost;Port=15432;Database=mainDb;Username=postgres",
                    ["ASPIRE_COMMAND_SCENARIO"] = "#8502 seed-data"
                }),
            commandOptions: new ProcessCommandOptions
            {
                Description = "Exercises a data seeding command with dashboard/CLI arguments.",
                IconName = "DatabasePlugConnected",
                MaxOutputLineCount = 20,
                Arguments =
                [
                    new InteractionInput
                    {
                        Name = "dataset",
                        Label = "Dataset",
                        Description = "Seed data size.",
                        InputType = InputType.Choice,
                        Value = "small",
                        Options =
                        [
                            KeyValuePair.Create("small", "Small"),
                            KeyValuePair.Create("medium", "Medium"),
                            KeyValuePair.Create("large", "Large")
                        ]
                    },
                    new InteractionInput
                    {
                        Name = "customers",
                        Label = "Customers",
                        Description = "Number of customers to create.",
                        InputType = InputType.Number,
                        Value = "25"
                    }
                ]
            });
        processCommands.WithProcessCommand(
            commandName: "issue-e2e-tests",
            displayName: "Issue: E2E test filter",
            processSpecFactory: context => CreateDotnetFileAppProcessSpec(
                processCommandIssueToolAppPath,
                arguments:
                [
                    "test",
                    context.Arguments.GetString("filter") ?? "smoke"
                ],
                environmentVariables: new Dictionary<string, string>
                {
                    ["ASPIRE_TEST_CONFIGURATION"] = "Debug",
                    ["ASPIRE_COMMAND_SCENARIO"] = "#8502 e2e-tests"
                }),
            commandOptions: new ProcessCommandOptions
            {
                Description = "Exercises running different test filters from a resource command.",
                IconName = "Beaker",
                MaxOutputLineCount = 20,
                Arguments =
                [
                    new InteractionInput
                    {
                        Name = "filter",
                        Label = "Filter",
                        Description = "The test filter to run.",
                        InputType = InputType.Choice,
                        Value = "smoke",
                        Options =
                        [
                            KeyValuePair.Create("smoke", "Smoke tests"),
                            KeyValuePair.Create("db", "Database tests"),
                            KeyValuePair.Create("ui", "UI tests")
                        ]
                    }
                ]
            });
        processCommands.WithProcessCommand(
            commandName: "issue-start-job",
            displayName: "Issue: Start job-like process",
            processSpecFactory: context => CreateDotnetFileAppProcessSpec(
                processCommandIssueToolAppPath,
                arguments:
                [
                    "job",
                    context.Arguments.GetString("job") ?? "daily-import"
                ],
                standardInputContent: context.Arguments.GetString("payload") ?? "local-trigger"),
            commandOptions: new ProcessCommandOptions
            {
                Description = "Exercises the on-demand job trigger shape by starting a local process with a payload.",
                IconName = "Play",
                MaxOutputLineCount = 20,
                Arguments =
                [
                    new InteractionInput
                    {
                        Name = "job",
                        Label = "Job",
                        Description = "Job name to run.",
                        InputType = InputType.Text,
                        Value = "daily-import",
                        MaxLength = 100
                    },
                    new InteractionInput
                    {
                        Name = "payload",
                        Label = "Payload",
                        Description = "Payload written to the job process standard input.",
                        InputType = InputType.Text,
                        Value = "local-trigger",
                        MaxLength = 200
                    }
                ]
            });
        processCommands.WithProcessCommand(
            commandName: "issue-restore-backup",
            displayName: "Issue: Restore backup",
            processSpecFactory: context => CreateNodeProcessSpec(
                processCommandBackupRestoreScriptPath,
                arguments:
                [
                    context.Arguments.GetString("backupPath") ?? processCommandSampleBackupPath,
                    context.Arguments.GetString("container") ?? "db-server",
                    context.Arguments.GetString("database") ?? "mainDb"
                ]),
            commandOptions: new ProcessCommandOptions
            {
                Description = "Exercises backup restore command composition similar to docker cp and docker exec pg_restore.",
                IconName = "DatabaseArrowRight",
                MaxOutputLineCount = 20,
                Arguments =
                [
                    new InteractionInput
                    {
                        Name = "backupPath",
                        Label = "Backup path",
                        Description = "Backup file path on the AppHost machine.",
                        InputType = InputType.Text,
                        Value = processCommandSampleBackupPath,
                        MaxLength = 500
                    },
                    new InteractionInput
                    {
                        Name = "container",
                        Label = "Container",
                        Description = "Container name or ID.",
                        InputType = InputType.Text,
                        Value = "db-server",
                        MaxLength = 100
                    },
                    new InteractionInput
                    {
                        Name = "database",
                        Label = "Database",
                        Description = "Database name to restore.",
                        InputType = InputType.Text,
                        Value = "mainDb",
                        MaxLength = 100
                    }
                ]
            });
        processCommands.WithProcessCommand(
            commandName: "issue-container-exec-shape",
            displayName: "Issue: Container exec shape",
            processSpecFactory: context => CreateNodeProcessSpec(
                processCommandContainerExecScriptPath,
                arguments:
                [
                    GetRequiredCommandArgument(context.Arguments, "container"),
                    GetRequiredCommandArgument(context.Arguments, "execCommand"),
                    GetRequiredCommandArgument(context.Arguments, "workingDirectory")
                ]),
            commandOptions: new ProcessCommandOptions
            {
                Description = "Exercises the local process shape for a docker exec-style command.",
                IconName = "WindowConsole",
                MaxOutputLineCount = 20,
                Arguments =
                [
                    new InteractionInput
                    {
                        Name = "container",
                        Label = "Container",
                        Description = "Container name or ID.",
                        InputType = InputType.Text,
                        Required = true,
                        Placeholder = "db-server",
                        MaxLength = 100
                    },
                    new InteractionInput
                    {
                        Name = "execCommand",
                        Label = "Command",
                        Description = "Command to execute in the container.",
                        InputType = InputType.Text,
                        Required = true,
                        Placeholder = "pg_isready -U postgres",
                        MaxLength = 200
                    },
                    new InteractionInput
                    {
                        Name = "workingDirectory",
                        Label = "Working directory",
                        Description = "Working directory inside the container.",
                        InputType = InputType.Text,
                        Required = true,
                        Placeholder = "/",
                        MaxLength = 200
                    }
                ]
            });

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
    }

    private static void AddTelemetryCommands(IDistributedApplicationBuilder builder, IResourceBuilder<ProjectResource> telemetryBuilder)
    {
        var interactionCommands = builder.AddCommandGroup("interaction-commands", telemetryBuilder.Resource);
        interactionCommands.AddInteractionCommands();

        var lifecycleCommands = builder.AddCommandGroup("lifecycle-commands", telemetryBuilder.Resource);
        lifecycleCommands
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
                    await ExecuteCommandForAllResourcesAsync(c.Services, KnownResourceCommands.StopCommand, c.CancellationToken);
                    return CommandResults.Success();
                },
                commandOptions: new() { IconName = "Stop", IconVariant = IconVariant.Filled })
            .WithCommand(
                name: "resource-start-all",
                displayName: "Start all resources",
                executeCommand: async (c) =>
                {
                    await ExecuteCommandForAllResourcesAsync(c.Services, KnownResourceCommands.StartCommand, c.CancellationToken);
                    return CommandResults.Success();
                },
                commandOptions: new() { IconName = "Play", IconVariant = IconVariant.Filled });

        var resultCommands = builder.AddCommandGroup("result-commands", telemetryBuilder.Resource);
        resultCommands
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
    }

    private static ExecuteCommandResult CreateJsonSuccess(string message, object payload, bool displayImmediately = false)
    {
        return CommandResults.Success(message, new CommandResultData
        {
            Value = SerializeCommandPayload(payload),
            Format = CommandResultFormat.Json,
            DisplayImmediately = displayImmediately
        });
    }

    private static ExecuteCommandResult CreateJsonFailure(string message, object payload)
    {
        return CommandResults.Failure(message, SerializeCommandPayload(payload), CommandResultFormat.Json);
    }

    private static string SerializeCommandPayload(object payload)
    {
        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }

    private static EchoCommandArguments ReadEchoCommandArguments(InteractionInputCollection arguments)
    {
        return new EchoCommandArguments
        {
            Message = arguments.GetString("message"),
            Repeat = arguments.GetInt32("repeat"),
            Shout = arguments.GetBoolean("shout"),
            Flavor = arguments.GetString("flavor") ?? "vanilla",
            Secret = arguments.GetString("secret")
        };
    }

    private static string GetRequiredCommandArgument(InteractionInputCollection arguments, string name)
    {
        return arguments.GetString(name) ?? throw new InvalidOperationException($"The '{name}' argument is required.");
    }

    private static ValidateCommandArguments ReadValidateCommandArguments(InteractionInputCollection arguments)
    {
        return new ValidateCommandArguments
        {
            Target = arguments.GetString("target"),
            Mode = arguments.GetString("mode") ?? "success",
            TimeoutSeconds = arguments.GetInt32("timeoutSeconds"),
            RequireHealthy = arguments.GetBoolean("requireHealthy")
        };
    }

    private static IReadOnlyList<InteractionInput> CreateArgumentStressInputs(int fieldCount)
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

    private static ProcessCommandSpec CreateNodeProcessSpec(
        string scriptPath,
        IReadOnlyList<string>? arguments = null,
        string? workingDirectory = null,
        IDictionary<string, string>? environmentVariables = null,
        bool inheritEnvironmentVariables = true,
        string? standardInputContent = null)
    {
        var processArguments = new List<string> { scriptPath };
        if (arguments is not null)
        {
            processArguments.AddRange(arguments);
        }

        return new ProcessCommandSpec(NodeExecutablePath)
        {
            Arguments = processArguments,
            WorkingDirectory = workingDirectory,
            EnvironmentVariables = environmentVariables ?? new Dictionary<string, string>(),
            InheritEnvironmentVariables = inheritEnvironmentVariables,
            StandardInputContent = standardInputContent
        };
    }

    private static ProcessCommandSpec CreateDotnetFileAppProcessSpec(
        string appPath,
        IReadOnlyList<string>? arguments = null,
        string? workingDirectory = null,
        IDictionary<string, string>? environmentVariables = null,
        bool inheritEnvironmentVariables = true,
        string? standardInputContent = null)
    {
        var processArguments = new List<string> { "run", "--file", appPath };
        if (arguments is { Count: > 0 })
        {
            processArguments.Add("--");
            processArguments.AddRange(arguments);
        }

        return new ProcessCommandSpec(DotnetExecutablePath)
        {
            Arguments = processArguments,
            WorkingDirectory = workingDirectory,
            EnvironmentVariables = environmentVariables ?? new Dictionary<string, string>(),
            InheritEnvironmentVariables = inheritEnvironmentVariables,
            StandardInputContent = standardInputContent
        };
    }

    private static async Task ExecuteCommandForAllResourcesAsync(IServiceProvider serviceProvider, string commandName, CancellationToken cancellationToken)
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

    private sealed class EchoCommandArguments
    {
        public string? Message { get; set; }

        public int Repeat { get; set; } = 1;

        public bool Shout { get; set; }

        public string Flavor { get; set; } = "vanilla";

        public string? Secret { get; set; }
    }

    private sealed class ValidateCommandArguments
    {
        public string? Target { get; set; }

        public string Mode { get; set; } = "success";

        public int TimeoutSeconds { get; set; } = 30;

        public bool RequireHealthy { get; set; } = true;
    }
}

#pragma warning restore ASPIREPROCESSCOMMAND001

