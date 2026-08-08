import * as vscode from 'vscode';
import { RunSessionInfo } from './dcp/types';

export type Capability =
    | 'prompting' // Support using VS Code to capture user input instead of CLI
    | 'baseline.v1'
    | 'secret-prompts.v1'
    | 'file-pickers.v1'
    | 'build-dotnet-using-cli.v2' // AppHost build ownership; see buildDotnetUsingCliCapability below for what advertising it means on each side
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
    | 'azure-functions'; // Support for running Azure Functions projects

export type Capabilities = Capability[];

/**
 * AppHost build ownership. The handshake is deliberately asymmetric: advertising this capability
 * means something different depending on which side of the backchannel does it.
 *
 * - The extension advertising it is a request - "the CLI owns the pre-build, I will not do it".
 *   `BuildAppHostIfNeededAsync` in the CLI reads that and builds. Without it the CLI stays out of
 *   the way, because an older extension still builds for itself.
 * - The CLI advertising it is a promise - "I pre-build the AppHost before every launch, debug and
 *   no-debug alike". `InteractionService` reads that and passes `forceBuild: false`, skipping the
 *   extension's own build.
 *
 * The guarantee is narrower than "exactly one build": it is that neither side ever skips the build
 * believing the other side did it, so no launch runs stale output. A redundant build is still
 * possible and is deliberately tolerated - a project-based AppHost with an `Executable` launch
 * profile always builds in `debugger/languages/dotnet.ts` (`shouldBuildProject` is true whenever
 * the project is not file-based), even when `forceBuild` is false, because that build compiles the
 * profile's dependencies rather than the AppHost output. A wasted incremental build there is
 * acceptable; a skipped one is not.
 *
 * The unversioned predecessor ('build-dotnet-using-cli', CLI 13.2.0-13.2.4) could not carry the
 * CLI-side promise: those versions advertised the token unconditionally but turned watch mode on
 * for a no-debug launch, which skipped the CLI pre-build entirely. An extension that believed the
 * token skipped its build too, nobody built, and the user silently ran stale output
 * (https://github.com/microsoft/aspire/issues/15850). The version suffix is what makes the promise
 * verifiable, so matching stays exact: never treat the unversioned token as proof the CLI built.
 */
export const buildDotnetUsingCliCapability = 'build-dotnet-using-cli.v2' satisfies Capability;

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
    // If you are resolving a merge conflict on this line, keep buildDotnetUsingCliCapability and do
    // not restore the unversioned 'build-dotnet-using-cli' next to it. Advertising the unversioned
    // token tells a CLI that only honors it that the extension has ceded the pre-build, and CLI
    // 13.2.0-13.2.4 then skipped that build on no-debug launches - which is exactly
    // https://github.com/microsoft/aspire/issues/15850 (silently launching stale output).
    // Guarded by 'AppHost build ownership advertises only the v2 capability' in test/capabilities.test.ts.
    const capabilities: Capabilities = ['prompting', 'baseline.v1', 'secret-prompts.v1', 'file-pickers.v1', buildDotnetUsingCliCapability];

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
