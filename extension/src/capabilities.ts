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
    | 'bun' // Support for running Bun projects
    | 'oven.bun-vscode' // Bun debug adapter extension identifier
    | 'browser' // Support for browser debugging (built-in to VS Code via js-debug)
    | 'maui' // Support for running .NET MAUI projects
    | 'ms-dotnettools.dotnet-maui' // MAUI debug adapter extension identifier
    | 'java' // Support for running Java projects
    | 'vscjava.vscode-java-debug' // Java debug adapter extension identifier
    | 'azure-functions'; // Support for running Azure Functions projects

export type Capabilities = Capability[];

export function isExtensionInstalled(extensionId: string): boolean {
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

// Rust debugging depends on a native debugger extension. Prefer the Microsoft C++ extension's
// Windows-only cppvsdbg engine when it is available, but CodeLLDB is also a valid Windows adapter
// and is required for GNU Rust targets. CodeLLDB remains the default on macOS/Linux. See:
// https://code.visualstudio.com/docs/languages/rust#_install-debugging-support
export function getRustExtensionId(
    platform: NodeJS.Platform = process.platform,
    extensionInstalled?: (extensionId: string) => boolean
): 'ms-vscode.cpptools' | 'vadimcn.vscode-lldb' {
    if (platform === 'win32'
        && extensionInstalled
        && !extensionInstalled('ms-vscode.cpptools')
        && extensionInstalled('vadimcn.vscode-lldb')) {
        return 'vadimcn.vscode-lldb';
    }

    return platform === 'win32' ? 'ms-vscode.cpptools' : 'vadimcn.vscode-lldb';
}

export function isRustInstalled(platform: NodeJS.Platform = process.platform) {
    return isExtensionInstalled(getRustExtensionId(platform, isExtensionInstalled));
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
