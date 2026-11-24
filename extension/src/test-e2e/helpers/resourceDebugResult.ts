import * as assert from 'assert';
import * as path from 'path';

export interface ResourceDebugToolResult {
    tool: 'aspire_resource_debug';
    success: boolean;
    outcome: string;
    appHost: string;
    resourceName: string;
    requestedStrategy: 'auto' | 'attach';
    effectiveStrategy: 'attach' | 'none';
    controller: 'editor' | 'none';
    provider?: 'dotnet' | 'go';
    debuggerExtensions?: Array<{ id: string; label: string }>;
}

export function assertSafeResourceDebugResult(result: ResourceDebugToolResult, requestedResourceName: string): void {
    const serialized = JSON.stringify(result);
    assert.deepStrictEqual(JSON.parse(serialized), result);
    assert.ok(!path.isAbsolute(result.appHost));
    // The tool echoes the caller's resource name, which can be a DCP instance name with a random suffix
    // (for example "e2e-go-ypduenvb"). Verify the echo exactly, then scan everything else for leaked details.
    assert.strictEqual(result.resourceName, requestedResourceName);
    const serializedWithoutResourceName = JSON.stringify({ ...result, resourceName: '' });
    assert.doesNotMatch(serializedWithoutResourceName, /(?:pid|process|configuration|arguments?|args|environment|env|secret|token|executable)|https?:\/\/|\/(?:Users|private|var|tmp)\b/i);
}
