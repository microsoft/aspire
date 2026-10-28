// Aspire TypeScript AppHost - E2E Demo with PostgreSQL and Express
// This demonstrates compute, databases, and references working together.
// Run with: aspire run
// Publish with: aspire publish

import { join } from 'node:path';
import { statSync } from 'node:fs';
import {
    createBuilder,
    refExpr,
    EnvironmentCallbackContext,
    ExecuteCommandContext,
    InputsDialogValidationContext,
    InputType,
    TerminalKeyName,
    TerminalPlacement
} from './.aspire/modules/aspire.mjs';

console.log("Aspire TypeScript AppHost starting...\n");

// Create the distributed application builder
const builder = await createBuilder();

const ec = await builder.executionContext();

const isPublishMode = await ec.isPublishMode();
console.log(`isRunMode: ${await ec.isRunMode()}`);
console.log(`isPublishMode: ${isPublishMode}`);

// Add Docker Compose environment for publishing
await builder.addDockerComposeEnvironment("compose");

const dir = await builder.appHostDirectory();
console.log(`AppHost directory: ${dir}`);
const processCommandScriptPath = join(dir, "process-command-scripts", "node-process-check.js");
const terminalScriptPath = join(dir, "terminal-scripts", "greeting.mjs");

// Add PostgreSQL server and database
const postgres = await builder.addPostgres("postgres");
const db = await postgres.addDatabase("db");

console.log("Added PostgreSQL server with database 'db'");

// Add Express API that connects to PostgreSQL (uses npm run dev with tsx)
const api = await builder
    .addNodeApp("api", "./express-api", "src/server.ts")
    .withRunScript("dev")
    .withHttpEndpoint({ env: "PORT" })
    .withReference(db)
    .waitFor(db);

console.log("Added Express API with reference to PostgreSQL database");

// Redis
const cache = await builder.addRedis("cache");
await cache.withPersistentLifetime();
await cache.withCommand(
    "terminal-dock",
    "Open AppHost terminal",
    async (context: ExecuteCommandContext) => {
        const terminals = await context.services().getTerminalService();
        const terminal = await terminals.createTerminal({
            title: "TypeScript AppHost terminal",
            executable: process.execPath,
            arguments: [terminalScriptPath],
            workingDirectory: dir,
            placement: TerminalPlacement.Dock
        });
        await terminal.start();
        await terminal.show();

        // The dock owns presentation, not lifetime: keep this terminal until its tab closes or the AppHost stops.
        return { success: true, message: `Opened terminal ${await terminal.id()}. Type a name or 'quit'.` };
    },
    {
        commandOptions: {
            description: "Creates an AppHost-owned terminal that stays in the dashboard dock.",
            iconName: "WindowConsole"
        }
    });
await cache.withCommand(
    "terminal-dialog",
    "Run terminal interaction",
    async (context: ExecuteCommandContext) => {
        const services = await context.services();
        const interactions = await services.getInteractionService();
        if (!(await interactions.isAvailable())) {
            return { success: false, message: "Interaction service is not available." };
        }
        const terminals = await services.getTerminalService();
        const terminal = await terminals.createTerminal({
            title: "Terminal setup",
            executable: process.execPath,
            arguments: [terminalScriptPath],
            workingDirectory: dir,
            placement: TerminalPlacement.Dialog
        });
        try {
            await terminal.start();
            const result = await interactions.promptTerminal(
                "The TypeScript AppHost will answer this terminal's prompt.",
                terminal,
                {
                    title: "Automated terminal interaction",
                    primaryButtonText: "Cancel",
                    work: async (workContext) => {
                        const cancellationToken = await workContext.cancellationToken();
                        await terminal.waitForText("Name: ", { timeout: 10000, cancellationToken });
                        await terminal.sendText("Aspire", { cancellationToken });
                        await terminal.sendKey(TerminalKeyName.Enter, { cancellationToken });
                        await terminal.waitForText("Hello, Aspire!", { timeout: 10000, cancellationToken });
                    }
                },
                await context.cancellationToken());

            if ((await terminals.getTerminal(await terminal.id())) === null) {
                throw new Error("The interaction unexpectedly disposed its borrowed terminal.");
            }
            return {
                success: !result.canceled,
                message: result.canceled ? "Terminal interaction canceled." : "Terminal interaction completed."
            };
        } finally {
            await terminal.dispose();
        }
    },
    {
        commandOptions: {
            description: "Drives a terminal through ATS while its dialog is displayed, then explicitly disposes it.",
            iconName: "WindowConsole"
        }
    });
await cache.withCommand(
    "set-prefix",
    "Set prefix",
    async (context: ExecuteCommandContext) => {
        const args = await context.arguments();
        const prefix = await args.requiredValue("prefix");
        const countValue = await args.requiredValue("count");
        const count = Number(countValue);

        return {
            success: true,
            message: `Validated prefix '${prefix}' with count ${count}.`
        };
    },
    {
        commandOptions: {
            description: "Validates ordered command arguments from the CLI and Dashboard.",
            arguments: [
                {
                    name: "prefix",
                    label: "Prefix",
                    inputType: InputType.Text,
                    required: true,
                    maxLength: 20
                },
                {
                    name: "count",
                    label: "Count",
                    inputType: InputType.Number,
                    required: true
                }
            ],
            validateArguments: async (context: InputsDialogValidationContext) => {
                const args = await context.inputs();
                const prefix = await args.requiredValue("prefix");
                const countValue = await args.requiredValue("count");
                const count = Number(countValue);

                if (prefix.toLowerCase() === "bad") {
                    await context.addValidationError("prefix", "Prefix cannot be 'bad'.");
                }

                if (!Number.isFinite(count) || count < 1) {
                    await context.addValidationError("count", "Count must be greater than or equal to 1.");
                }
            }
        }
    });
await cache.withProcessCommand(
    "node-process-check",
    "Node process check",
    {
        executablePath: "node",
        arguments: [
            processCommandScriptPath,
            "from-typescript-apphost"
        ],
        environmentVariables: {
            TS_PROCESS_COMMAND_SAMPLE: "from-process-command"
        },
        standardInputContent: "hello from TypeScript AppHost",
        maxOutputLineCount: 10,
        commandOptions: {
            description: "Runs a Node process command from the TypeScript AppHost.",
            iconName: "WindowConsole"
        }
    });
await cache.withProcessCommandFactory(
    "node-process-check-factory",
    "Node process check with arguments",
    async (context: ExecuteCommandContext) => {
        const args = await context.arguments();
        const message = await args.requiredValue("message");

        return {
            executablePath: "node",
            arguments: [
                processCommandScriptPath,
                message
            ],
            environmentVariables: {
                TS_PROCESS_COMMAND_SAMPLE: "from-process-command-factory"
            },
            standardInputContent: "hello from TypeScript AppHost factory"
        };
    },
    {
        commandOptions: {
            description: "Runs a Node process command with arguments from the TypeScript AppHost.",
            iconName: "WindowConsole",
            arguments: [
                {
                    name: "message",
                    label: "Message",
                    inputType: InputType.Text,
                    required: true
                }
            ]
        },
        maxOutputLineCount: 10
    });
await cache.withCommand(
    "upload-file",
    "Upload file",
    async (context: ExecuteCommandContext) => {
        const interactionService = await context.services().getInteractionService();

        if (!(await interactionService.isAvailable())) {
            return { success: true, message: "Interaction service is not available." };
        }

        const fileInput = await interactionService.createFileInput("file", {
            label: "Select a file",
            description: "Choose a file to upload",
            required: true,
            maxFileSize: 10 * 1024 * 1024 // 10 MB
        });

        const result = await interactionService.promptInput(
            "Upload file",
            "Select a file to upload to the resource.",
            fileInput,
            {
                primaryButtonText: "Upload"
            });

        if (result.canceled) {
            return { success: false, message: "File upload was canceled." };
        }

        const files = result.input?.files ?? [];
        const names = files.map(f => f.name).join(", ");
        const totalSize = files.reduce((sum, f) => sum + (f.filePath ? statSync(f.filePath).size : 0), 0);
        return { success: true, message: `Uploaded ${files.length} file(s): ${names} (${totalSize} bytes)` };
    },
    {
        commandOptions: {
            description: "Prompts the user to select a file and uploads it.",
            iconName: "DocumentAdd"
        }
    });
await cache.withCommand(
    "show-progress",
    "Show progress",
    async (context: ExecuteCommandContext) => {
        const interactionService = await context.services().getInteractionService();

        if (!(await interactionService.isAvailable())) {
            return { success: true, message: "Interaction service is not available." };
        }

        const result = await interactionService.promptProgress(
            "Please wait while data is being processed...",
            {
                title: "Processing",
                primaryButtonText: "Cancel",
                work: async () => {
                    // Simulate a long-running operation.
                    await new Promise<void>(resolve => setTimeout(resolve, 10000));
                }
            });

        if (result.canceled) {
            return { success: false, message: "Operation was canceled by the user." };
        }

        return { success: true, message: "Processing complete!" };
    },
    {
        commandOptions: {
            description: "Shows a progress dialog with a cancel button that completes after 10 seconds.",
            iconName: "ArrowSync"
        }
    });
await cache.withCommand(
    "auto-progress",
    "Auto progress",
    async() => {
        await new Promise<void>(resolve => setTimeout(resolve, 10000));
        return { success: true, message: "Done!" };
    },
    {
        commandOptions: {
            description: "Automatically shows a progress dialog via CommandProgressOptions.",
            iconName: "ArrowSync",
            progress: {
                message: "Running automated task...",
                title: "Auto Progress"
            }
        }
    });

console.log("Added Redis cache");

// Vite frontend
await builder
    .addViteApp("frontend", "./vite-frontend")
    .withReference(api)
    .waitFor(api)
    .withEnvironment("CUSTOM_ENV", "value")
    .withEnvironmentCallback(async (ctx: EnvironmentCallbackContext) => {
        // await needed here because getEndpoint returns a value we use
        const ep = await api.getEndpoint("http");
        await ctx.environment().set("API_ENDPOINT", refExpr`${ep}`);
    });

console.log("Added Vite frontend with reference to API");

// build() flushes all pending promises before running
await builder.build().run();
