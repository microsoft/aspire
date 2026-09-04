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
    | 'rust' // Support for running Rust projects
    | 'ms-vscode.cpptools' // Rust debug adapter extension identifier on Windows (cppvsdbg)
    | 'vadimcn.vscode-lldb' // Rust debug adapter extension identifier on macOS/Linux (CodeLLDB)
    | 'node' // Support for running Node.js projects
    | 'deno.v1' // Support for debugging Deno AppHosts through js-debug's inspector attach path
    | 'bun' // Support for running Bun projects
    | 'oven.bun-vscode' // Bun debug adapter extension identifier
    | 'deno' // Support for running Deno projects (built-in to VS Code via js-debug)
    | 'browser' // Support for browser debugging (built-in to VS Code via js-debug)
    | 'maui' // Support for running .NET MAUI projects
    | 'ms-dotnettools.dotnet-maui' // MAUI debug adapter extension identifier
    | 'java' // Support for running Java projects
    | 'vscjava.vscode-java-debug' // Java debug adapter extension identifier
    | 'azure-functions' // Support for running Azure Functions projects
    | 'message-actions.v1' // Support structured actions on interaction-service notifications
    | 'apphost-log-output.v1'; // Support structured AppHost log correlation in the debug console

export type Capabilities = Capability[];

export function isExtensionInstalled(extensionId: string): boolean {
    const extension = vscode.extensions.getExtension(extensionId);
    return !!extension;
}

export function isCsDevKitInstalled() {
    return isExtensionInstalled("ms-dotnettools.csdevkit");
}

export const csharpExtensionId = 'ms-dotnettools.csharp';
export const azureFunctionsExtensionId = 'ms-azuretools.vscode-azurefunctions';
export const mauiExtensionId = 'ms-dotnettools.dotnet-maui';
export const codeLldbExtensionId = 'vadimcn.vscode-lldb';

export function isCsharpInstalled() {
    return isExtensionInstalled(csharpExtensionId);
}

export function isPythonInstalled() {
    return isExtensionInstalled("ms-python.debugpy");
}

export function isGoInstalled() {
    return isExtensionInstalled("golang.go");
}

// Rust debugging depends on a native debugger extension. Prefer the Microsoft C++ extension's
// Windows-only cppvsdbg engine when it is available, but CodeLLDB is also a valid Windows adapter
// and is required for GNU Rust targets. CodeLLDB remains the default on macOS/Linux. See:
// https://code.visualstudio.com/docs/languages/rust#_install-debugging-support
export function getRustExtensionId(
    platform: NodeJS.Platform = process.platform,
    extensionInstalled?: (extensionId: string) => boolean
): 'ms-vscode.cpptools' | typeof codeLldbExtensionId {
    if (platform === 'win32'
        && extensionInstalled
        && !extensionInstalled('ms-vscode.cpptools')
        && extensionInstalled(codeLldbExtensionId)) {
        return codeLldbExtensionId;
    }

    return platform === 'win32' ? 'ms-vscode.cpptools' : codeLldbExtensionId;
}

export function isRustInstalled(platform: NodeJS.Platform = process.platform) {
    return isExtensionInstalled(getRustExtensionId(platform, isExtensionInstalled));
}

export function isAzureFunctionsExtensionInstalled() {
    return isExtensionInstalled(azureFunctionsExtensionId);
}

export function isMauiInstalled() {
    return isExtensionInstalled(mauiExtensionId);
}

export function isNodeInstalled() {
    // Node.js debugging uses VS Code's built-in js-debug, no extension needed
    return true;
}

export function isBunInstalled() {
    return isExtensionInstalled("oven.bun-vscode");
}

// The Java debug adapter cannot launch anything on its own: it resolves main classes, the
// classpath and project metadata through the redhat.java language server, which is why
// vscjava.vscode-java-debug declares redhat.java as an extension dependency and both ship together
// in the "Extension Pack for Java". java.ts also calls the redhat.java API directly to refresh the
// project configuration, so advertise Java support only when both are present.
// https://github.com/microsoft/vscode-java-debug#requirements
export const javaLanguageExtensionId = 'redhat.java';
export const javaDebugExtensionId = 'vscjava.vscode-java-debug';

export function isJavaInstalled(extensionInstalled: (extensionId: string) => boolean = isExtensionInstalled) {
    return extensionInstalled(javaLanguageExtensionId) && extensionInstalled(javaDebugExtensionId);
}

export function getSupportedCapabilities(platform: NodeJS.Platform = process.platform): Capabilities {
    const capabilities: Capabilities = ['prompting', 'baseline.v1', 'secret-prompts.v1', 'file-pickers.v1', 'message-actions.v1', 'build-dotnet-using-cli'];

    capabilities.push('apphost-log-output.v1');

    if (isCsDevKitInstalled()) {
        capabilities.push("devkit");
        capabilities.push("ms-dotnettools.csdevkit");
    }

    if (isCsharpInstalled()) {
        capabilities.push("project");
        capabilities.push(csharpExtensionId);

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

    if (isRustInstalled(platform)) {
        const rustExtensionId = getRustExtensionId(platform, isExtensionInstalled);
        capabilities.push("rust");
        capabilities.push(rustExtensionId);
    }

    if (isNodeInstalled()) {
        capabilities.push("node");
        capabilities.push("deno.v1");
        capabilities.push("browser");
    }

    if (isBunInstalled()) {
        capabilities.push("bun");
        capabilities.push("oven.bun-vscode");
    }

    // Deno debugging uses VS Code's built-in js-debug, so no extension probe is required.
    capabilities.push("deno");

    if (isMauiInstalled()) {
        capabilities.push("maui");
        capabilities.push(mauiExtensionId);
    }

    if (isJavaInstalled()) {
        capabilities.push("java");
        capabilities.push(javaDebugExtensionId);
    }

    return capabilities;
}

export function getRunSessionInfo(): RunSessionInfo {
    return {
        protocols_supported: ["2024-03-03", "2024-04-23", "2025-10-01"],
        supported_launch_configurations: getSupportedCapabilities()
    };
}
