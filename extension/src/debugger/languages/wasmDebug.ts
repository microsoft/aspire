import * as vscode from 'vscode';
import { AspireResourceExtendedDebugConfiguration } from '../../dcp/types';
import { extensionLogOutputChannel } from '../../utils/logging';
import { AspireDebugSession } from '../AspireDebugSession';

const CSHARP_EXTENSION_ID = 'ms-dotnettools.csharp';

interface CSharpExtensionExports {
    tryToUseVSDbgForMono?: (url: string, projectPath: string) => Promise<[string, number, number]>;
}

/**
 * Attempts to start a managed WebAssembly debug session via the C# extension's
 * VSWebAssemblyBridge. If successful, modifies the browser debug configuration
 * to connect through the bridge and starts a companion monovsdbg_wasm session
 * for .NET IL debugging.
 *
 * The monovsdbg_wasm session is started as a child of the parent Aspire debug session
 * so it is properly tracked and terminated when the Aspire session ends.
 *
 * @param url The application URL to debug
 * @param projectPath Path to the Blazor WASM client .csproj
 * @param debugConfiguration The browser debug configuration to modify
 * @param parentDebugSession The parent Aspire debug session (used as parent for child sessions)
 * @returns true if WASM debugging was successfully wired up, false if falling back to plain browser
 */
export async function tryStartWasmDebugging(
    url: string,
    projectPath: string,
    debugConfiguration: AspireResourceExtendedDebugConfiguration,
    parentDebugSession: AspireDebugSession
): Promise<boolean> {
    const csharpExt = vscode.extensions.getExtension<CSharpExtensionExports>(CSHARP_EXTENSION_ID);
    if (!csharpExt) {
        extensionLogOutputChannel.warn('C# extension not installed — skipping WASM debugging');
        return false;
    }

    if (!csharpExt.isActive) {
        await csharpExt.activate();
    }

    if (!csharpExt.exports?.tryToUseVSDbgForMono) {
        extensionLogOutputChannel.warn('C# extension does not export tryToUseVSDbgForMono — skipping WASM debugging');
        return false;
    }

    let inspectUri: string;
    let portICorDebug: number;
    let portBrowserDebug: number;

    try {
        [inspectUri, portICorDebug, portBrowserDebug] =
            await csharpExt.exports.tryToUseVSDbgForMono(url, projectPath);
    } catch (e) {
        extensionLogOutputChannel.warn(`tryToUseVSDbgForMono threw: ${e}`);
        return false;
    }

    if (inspectUri === '') {
        extensionLogOutputChannel.info('tryToUseVSDbgForMono returned empty inspectUri — falling back to plain browser');
        return false;
    }

    extensionLogOutputChannel.info(
        `WASM debug bridge ready — inspectUri: ${inspectUri}, iCorDebug port: ${portICorDebug}, browser port: ${portBrowserDebug}`
    );

    // Start the managed WASM debug session (monovsdbg_wasm).
    // This connects to the bridge's iCorDebug port and provides .NET IL debugging
    // (breakpoints, stepping, locals, etc.) for code running in the browser.
    const wasmManagedConfig: vscode.DebugConfiguration = {
        name: `${debugConfiguration.name} Wasm Managed`,
        type: 'monovsdbg_wasm',
        request: 'launch',
        monoDebuggerOptions: {
            ip: '127.0.0.1',
            port: portICorDebug,
            platform: 'browser',
            isServer: true,
        },
        // Cascade termination: when the browser session ends, terminate the managed debugger too
        cascadeTerminateToConfigurations: [debugConfiguration.name],
    };

    extensionLogOutputChannel.info(`[WASM] Attempting to start monovsdbg_wasm session with config: ${JSON.stringify(wasmManagedConfig)}`);

    // Start the monovsdbg_wasm session as a child of the Aspire debug session.
    // This ensures it's tracked and terminated when the Aspire session ends.
    const debugSessionStarted = await vscode.debug.startDebugging(
        vscode.workspace.workspaceFolders?.[0], wasmManagedConfig, { parentSession: parentDebugSession.session });
    if (!debugSessionStarted) {
        extensionLogOutputChannel.warn('Failed to start monovsdbg_wasm session — falling back to plain browser');
        return false;
    }

    // Use the inspectUri returned by the bridge directly.
    // The bridge acts as the debug proxy (replaces /_framework/debug/ws-proxy).
    // The inspectUri contains js-debug placeholders like {browserInspectUriPath}
    // which js-debug resolves at runtime using the browser's DevTools WebSocket path.
    debugConfiguration.inspectUri = inspectUri;

    // Tell js-debug to launch the browser with remote debugging on the port
    // the bridge expects. The bridge connects to the browser on this port to
    // proxy DevTools protocol messages.
    (debugConfiguration as any).port = portBrowserDebug;

    // The WASM runtime checks for a debugger connection at startup. Since the browser
    // launches before the debugger attaches, the runtime initializes with debugging
    // disabled. We must reload the page after the browser debug session connects so
    // the runtime re-initializes and calls mono_wasm_enable_debugging().
    const browserSessionName = debugConfiguration.name;
    const disposable = vscode.debug.onDidStartDebugSession(async (session) => {
        if (session.name === browserSessionName && session.type === debugConfiguration.type) {
            disposable.dispose();
            extensionLogOutputChannel.info(`[WASM] Browser session started — reloading page to activate WASM debugging`);
            // Give the browser a moment to fully connect before reloading
            await new Promise(resolve => setTimeout(resolve, 1000));
            try {
                // Use the DAP custom request to reload the page via CDP
                await session.customRequest('evaluate', {
                    expression: 'location.reload()',
                    context: 'repl',
                });
                extensionLogOutputChannel.info(`[WASM] Page reload triggered`);
            } catch (e) {
                extensionLogOutputChannel.warn(`[WASM] Failed to reload page: ${e}`);
            }
        }
    });

    return true;
}
