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
    | 'node' // Support for running Node.js projects
    | 'browser' // Support for browser debugging (built-in to VS Code via js-debug)
    | 'azure-functions'; // Support for running Azure Functions projects

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

export function isAzureFunctionsExtensionInstalled() {
    return isExtensionInstalled("ms-azuretools.vscode-azurefunctions");
}

export function isNodeInstalled() {
    // Node.js debugging uses VS Code's built-in js-debug, no extension needed
    return true;
}

export function getSupportedCapabilities(): Capabilities {
    const capabilities: Capabilities = ['prompting', 'baseline.v1', 'secret-prompts.v1', 'file-pickers.v1', 'build-dotnet-using-cli'];

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

    if (isNodeInstalled()) {
        capabilities.push("node");
        capabilities.push("browser");
    }

    return capabilities;
}

export function getRunSessionInfo(): RunSessionInfo {
    return {
        protocols_supported: ["2024-03-03", "2024-04-23", "2025-10-01"],
        supported_launch_configurations: getSupportedCapabilities()
    };
}
