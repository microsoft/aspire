// Shared by the extension host and the separate ExTester process. Keep this
// module free of VS Code imports so both sides use the same timeout contract.
export const blazorWasmDebugProofTimeoutMs = 300000;
export const blazorWasmDebugProofResponseAllowanceMs = 30000;

export function getBlazorWasmDebugProofCleanupTimeoutMs(proofTimeoutMs: number): number {
    return Math.min(proofTimeoutMs, 30000);
}

export function getBlazorWasmDebugProofControlTimeoutMs(proofTimeoutMs: number): number {
    return proofTimeoutMs + getBlazorWasmDebugProofCleanupTimeoutMs(proofTimeoutMs) + blazorWasmDebugProofResponseAllowanceMs;
}
