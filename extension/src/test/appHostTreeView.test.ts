import * as assert from 'assert';
import { shortenPath } from '../views/AppHostDataRepository';
import { getResourceContextValue, getResourceIcon } from '../views/AspireAppHostTreeProvider';
import type { ResourceJson } from '../views/AppHostDataRepository';
import { ResourceState, HealthStatus, StateStyle } from '../editor/resourceConstants';

function makeResource(overrides: Partial<ResourceJson> = {}): ResourceJson {
    const base: ResourceJson = {
        name: 'my-service',
        displayName: null,
        resourceType: 'Project',
        state: null,
        stateStyle: null,
        healthStatus: null,
        dashboardUrl: null,
        urls: null,
        commands: null,
        properties: null,
    };
    return { ...base, ...overrides } as ResourceJson;
}

suite('shortenPath', () => {
    test('.csproj returns just the filename', () => {
        assert.strictEqual(shortenPath('/home/user/repos/MyApp/MyApp.AppHost.csproj'), 'MyApp.AppHost.csproj');
    });

    test('.csproj with backslashes returns just the filename', () => {
        assert.strictEqual(shortenPath('C:\\Users\\dev\\MyApp\\MyApp.AppHost.csproj'), 'MyApp.AppHost.csproj');
    });

    test('non-csproj returns parent/filename', () => {
        assert.strictEqual(shortenPath('/home/user/repos/MyApp/AppHost.cs'), 'MyApp/AppHost.cs');
    });

    test('non-csproj with backslashes returns parent/filename', () => {
        assert.strictEqual(shortenPath('C:\\Users\\dev\\MyApp\\AppHost.cs'), 'MyApp/AppHost.cs');
    });

    test('single segment returns as-is', () => {
        assert.strictEqual(shortenPath('AppHost.cs'), 'AppHost.cs');
    });

    test('two segments returns parent/filename', () => {
        assert.strictEqual(shortenPath('MyApp/AppHost.cs'), 'MyApp/AppHost.cs');
    });
});

suite('getResourceContextValue', () => {
    test('resource with no commands returns just "resource"', () => {
        assert.strictEqual(getResourceContextValue(makeResource()), 'resource');
    });

    test('resource with start command', () => {
        const result = getResourceContextValue(makeResource({
            commands: { 'start': { description: null } },
        }));
        assert.strictEqual(result, 'resource:canStart');
    });

    test('resource with resource-start command', () => {
        const result = getResourceContextValue(makeResource({
            commands: { 'resource-start': { description: null } },
        }));
        assert.strictEqual(result, 'resource:canStart');
    });

    test('resource with stop command', () => {
        const result = getResourceContextValue(makeResource({
            commands: { 'stop': { description: null } },
        }));
        assert.strictEqual(result, 'resource:canStop');
    });

    test('resource with all lifecycle commands', () => {
        const result = getResourceContextValue(makeResource({
            commands: {
                'start': { description: null },
                'stop': { description: null },
                'restart': { description: null },
            },
        }));
        assert.strictEqual(result, 'resource:canStart:canStop:canRestart');
    });

    test('resource with non-lifecycle commands has base context only', () => {
        const result = getResourceContextValue(makeResource({
            commands: { 'custom-action': { description: 'do something' } },
        }));
        assert.strictEqual(result, 'resource');
    });

    test('resource with mixed lifecycle and custom commands', () => {
        const result = getResourceContextValue(makeResource({
            commands: {
                'restart': { description: null },
                'custom-action': { description: null },
            },
        }));
        assert.strictEqual(result, 'resource:canRestart');
    });

});

suite('getResourceIcon', () => {
    test('Running + Healthy shows pass icon', () => {
        const icon = getResourceIcon(makeResource({ state: ResourceState.Running, healthStatus: HealthStatus.Healthy }));
        assert.strictEqual(icon.id, 'pass');
    });

    test('Running + Unhealthy shows error icon', () => {
        const icon = getResourceIcon(makeResource({ state: ResourceState.Running, healthStatus: HealthStatus.Unhealthy }));
        assert.strictEqual(icon.id, 'error');
    });

    test('Running + Degraded shows warning icon', () => {
        const icon = getResourceIcon(makeResource({ state: ResourceState.Running, healthStatus: HealthStatus.Degraded }));
        assert.strictEqual(icon.id, 'warning');
    });

    test('Running + error stateStyle shows error icon', () => {
        const icon = getResourceIcon(makeResource({ state: ResourceState.Running, stateStyle: StateStyle.Error }));
        assert.strictEqual(icon.id, 'error');
    });

    test('Running + warning stateStyle shows warning icon', () => {
        const icon = getResourceIcon(makeResource({ state: ResourceState.Running, stateStyle: StateStyle.Warning }));
        assert.strictEqual(icon.id, 'warning');
    });

    test('Active state treated same as Running', () => {
        const icon = getResourceIcon(makeResource({ state: ResourceState.Active }));
        assert.strictEqual(icon.id, 'pass');
    });

    test('Finished shows circle-outline', () => {
        const icon = getResourceIcon(makeResource({ state: ResourceState.Finished }));
        assert.strictEqual(icon.id, 'circle-outline');
    });

    test('Exited with error stateStyle shows error', () => {
        const icon = getResourceIcon(makeResource({ state: ResourceState.Exited, stateStyle: StateStyle.Error }));
        assert.strictEqual(icon.id, 'error');
    });

    test('FailedToStart shows error icon', () => {
        const icon = getResourceIcon(makeResource({ state: ResourceState.FailedToStart }));
        assert.strictEqual(icon.id, 'error');
    });

    test('RuntimeUnhealthy shows error icon', () => {
        const icon = getResourceIcon(makeResource({ state: ResourceState.RuntimeUnhealthy }));
        assert.strictEqual(icon.id, 'error');
    });

    test('Starting shows loading spinner', () => {
        const icon = getResourceIcon(makeResource({ state: ResourceState.Starting }));
        assert.strictEqual(icon.id, 'loading~spin');
    });

    test('Building shows loading spinner', () => {
        const icon = getResourceIcon(makeResource({ state: ResourceState.Building }));
        assert.strictEqual(icon.id, 'loading~spin');
    });

    test('null state shows circle-outline', () => {
        const icon = getResourceIcon(makeResource({ state: null }));
        assert.strictEqual(icon.id, 'circle-outline');
    });

    test('unknown state shows circle-filled', () => {
        const icon = getResourceIcon(makeResource({ state: 'SomeUnknownState' }));
        assert.strictEqual(icon.id, 'circle-filled');
    });
});
