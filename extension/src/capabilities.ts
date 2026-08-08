import * as vscode from 'vscode';
import { RunSessionInfo } from './dcp/types';

export type Capability =
    | 'prompting' // Support using VS Code to capture user input instead of CLI
    | 'baseline.v1'
    | 'secret-prompts.v1'
    | 'file-pickers.v1'
    | 'build-dotnet-using-cli' // Support building .NET projects using the CLI
    | 'devkit' // Support for .NET DevKit extension (old, used for determining whether to build .NET projects in extension)
    | 'ms-dotnettools.csdevkit' // Older AppHost versions used this extension identifier instead of devkit
    | 'project' // Support for running C# projects
    | 'ms-dotnettools.csharp' // Older AppHost versions used this extension identifier instead of project
    | 'python' // Support for running Python projects
    | 'ms-python.python' // Older AppHost versions used this extension identifier instead of python
    | 'go' // Support for running Go projects
    | 'golang.go' // Older AppHost versions used this extension identifier instead of go
    | 'node' // Support for running Node.js projects
    | 'bun' // Support for running Bun projects
    | 'oven.bun-vscode' // Bun debug adapter extension identifier
    | 'browser' // Support for browser debugging (built-in to VS Code via js-debug)
    | 'maui' // Support for running .NET MAUI projects
    | 'ms-dotnettools.dotnet-maui' // MAUI debug adapter extension identifier
    | 'azure-functions' // Support for running Azure Functions projects
    | 'apphost-log-output.v1'; // Support structured AppHost log correlation in the debug console

export type Capabilities = Capability[];

function isExtensionInstalled(extensionId: string): boolean {
    const extension = vscode.extensions.getExtension(extensionId);
    return !!extension;
}

export function isCsDevKitInstalled() {
    return isExtensionInstalled("ms-dotnettools.csdevkit");
}

export function isCsharpInstalled() {
    return isExtensionInstalled("ms-dotnettools.csharp");
}

export function isPythonInstalled() {
    return isExtensionInstalled("ms-python.python");
}

export function isGoInstalled() {
    return isExtensionInstalled("golang.go");
}

export function isAzureFunctionsExtensionInstalled() {
    return isExtensionInstalled("ms-azuretools.vscode-azurefunctions");
}

export function isMauiInstalled() {
    return isExtensionInstalled("ms-dotnettools.dotnet-maui");
}

export function isNodeInstalled() {
    // Node.js debugging uses VS Code's built-in js-debug, no extension needed
    return true;
}

export function isBunInstalled() {
    return isExtensionInstalled("oven.bun-vscode");
}

export function getSupportedCapabilities(): Capabilities {
    const capabilities: Capabilities = ['prompting', 'baseline.v1', 'secret-prompts.v1', 'file-pickers.v1', 'build-dotnet-using-cli'];

    // Pushed rather than added to the literal above so this feature never has to be reconciled
    // with the build-ownership token that shares that line. Resolving a conflict there by keeping
    // both sides is what would restore the unversioned 'build-dotnet-using-cli' and with it
    // https://github.com/microsoft/aspire/issues/15850, so the line is left to the change that
    // owns it. Capability tokens here are versioned per feature; an unversioned token silently
    // fails to match the other side rather than erroring. test/appHostLogOutputCapability.test.ts
    // enforces that for this token.
    capabilities.push('apphost-log-output.v1');

    if (isCsDevKitInstalled()) {
        capabilities.push("devkit");
        capabilities.push("ms-dotnettools.csdevkit");
    }

    if (isCsharpInstalled()) {
        capabilities.push("project");
        capabilities.push("ms-dotnettools.csharp");

        // Azure Functions debugging requires both C# (coreclr attach to the worker
        // process) and the Azure Functions extension (to launch func host start).
        if (isAzureFunctionsExtensionInstalled()) {
            capabilities.push("azure-functions");
        }
    }

    if (isPythonInstalled()) {
        capabilities.push("python");
        capabilities.push("ms-python.python");
    }

    if (isGoInstalled()) {
        capabilities.push("go");
        capabilities.push("golang.go");
    }

    if (isNodeInstalled()) {
        capabilities.push("node");
        capabilities.push("browser");
    }

    if (isBunInstalled()) {
        capabilities.push("bun");
        capabilities.push("oven.bun-vscode");
    }

    if (isMauiInstalled()) {
        capabilities.push("maui");
        capabilities.push("ms-dotnettools.dotnet-maui");
    }

    return capabilities;
}

export function getRunSessionInfo(): RunSessionInfo {
    return {
        protocols_supported: ["2024-03-03", "2024-04-23", "2025-10-01"],
        supported_launch_configurations: getSupportedCapabilities()
    };
}
