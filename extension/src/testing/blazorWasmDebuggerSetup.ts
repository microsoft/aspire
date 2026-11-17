export type BlazorWasmDebuggerStatus = 'ready' | 'missing-bridge';

interface BlazorWasmDebuggerSetup {
    prepare: () => Promise<unknown>;
    reloadWindow: () => Promise<void>;
    onRetry: (attempt: number) => void;
}

export async function ensureBlazorWasmDebuggerReady(setup: BlazorWasmDebuggerSetup): Promise<void> {
    const attempts = 3;
    for (let attempt = 1; attempt <= attempts; attempt++) {
        const status = await setup.prepare();
        if (status === 'ready') {
            return;
        }
        if (status !== 'missing-bridge') {
            throw new Error('The Blazor debugger setup returned an unexpected readiness response.');
        }

        if (attempt < attempts) {
            // C# installs this component during activation. Reactivate in a fresh extension host
            // only for a missing component, before any AppHost or managed proof has been started.
            setup.onRetry(attempt);
            await setup.reloadWindow();
        }
    }

    throw new Error('VSWebAssemblyBridge is still missing after 3 C# activations. See the C# output for dependency installation errors.');
}
