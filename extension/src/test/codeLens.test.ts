import * as assert from 'assert';
import { getCodeLensStateLabel } from '../editor/AspireCodeLensProvider';
import {
    codeLensResourceRunning,
    codeLensResourceRunningWarning,
    codeLensResourceRunningError,
    codeLensResourceStarting,
    codeLensResourceStopped,
    codeLensResourceStoppedError,
    codeLensResourceError,
} from '../loc/strings';
import { ResourceState, StateStyle } from '../editor/resourceConstants';

suite('getCodeLensStateLabel', () => {
    // --- Running / Active states ---

    test('Running with no stateStyle returns running label', () => {
        assert.strictEqual(getCodeLensStateLabel(ResourceState.Running, ''), codeLensResourceRunning);
    });

    test('Active with no stateStyle returns running label', () => {
        assert.strictEqual(getCodeLensStateLabel(ResourceState.Active, ''), codeLensResourceRunning);
    });

    test('Running with error stateStyle returns running-error label', () => {
        assert.strictEqual(getCodeLensStateLabel(ResourceState.Running, StateStyle.Error), codeLensResourceRunningError);
    });

    test('Running with warning stateStyle returns running-warning label', () => {
        assert.strictEqual(getCodeLensStateLabel(ResourceState.Running, StateStyle.Warning), codeLensResourceRunningWarning);
    });

    test('Active with error stateStyle returns running-error label', () => {
        assert.strictEqual(getCodeLensStateLabel(ResourceState.Active, StateStyle.Error), codeLensResourceRunningError);
    });

    test('Active with warning stateStyle returns running-warning label', () => {
        assert.strictEqual(getCodeLensStateLabel(ResourceState.Active, StateStyle.Warning), codeLensResourceRunningWarning);
    });

    // --- Starting states ---

    test('Starting returns starting label', () => {
        assert.strictEqual(getCodeLensStateLabel(ResourceState.Starting, ''), codeLensResourceStarting);
    });

    test('Building returns starting label', () => {
        assert.strictEqual(getCodeLensStateLabel(ResourceState.Building, ''), codeLensResourceStarting);
    });

    test('Waiting returns starting label', () => {
        assert.strictEqual(getCodeLensStateLabel(ResourceState.Waiting, ''), codeLensResourceStarting);
    });

    test('NotStarted returns starting label', () => {
        assert.strictEqual(getCodeLensStateLabel(ResourceState.NotStarted, ''), codeLensResourceStarting);
    });

    // --- Error states ---

    test('FailedToStart returns error label', () => {
        assert.strictEqual(getCodeLensStateLabel(ResourceState.FailedToStart, ''), codeLensResourceError);
    });

    test('RuntimeUnhealthy returns error label', () => {
        assert.strictEqual(getCodeLensStateLabel(ResourceState.RuntimeUnhealthy, ''), codeLensResourceError);
    });

    // --- Stopped states ---

    test('Finished with no stateStyle returns stopped label', () => {
        assert.strictEqual(getCodeLensStateLabel(ResourceState.Finished, ''), codeLensResourceStopped);
    });

    test('Exited with no stateStyle returns stopped label', () => {
        assert.strictEqual(getCodeLensStateLabel(ResourceState.Exited, ''), codeLensResourceStopped);
    });

    test('Stopping with no stateStyle returns stopped label', () => {
        assert.strictEqual(getCodeLensStateLabel(ResourceState.Stopping, ''), codeLensResourceStopped);
    });

    test('Finished with error stateStyle returns stopped-error label', () => {
        assert.strictEqual(getCodeLensStateLabel(ResourceState.Finished, StateStyle.Error), codeLensResourceStoppedError);
    });

    test('Exited with error stateStyle returns stopped-error label', () => {
        assert.strictEqual(getCodeLensStateLabel(ResourceState.Exited, StateStyle.Error), codeLensResourceStoppedError);
    });

    test('Stopping with error stateStyle returns stopped-error label', () => {
        assert.strictEqual(getCodeLensStateLabel(ResourceState.Stopping, StateStyle.Error), codeLensResourceStoppedError);
    });

    // --- Default / unknown states ---

    test('unknown state returns the state string itself', () => {
        assert.strictEqual(getCodeLensStateLabel('SomeUnknownState', ''), 'SomeUnknownState');
    });

    test('empty state returns stopped label', () => {
        assert.strictEqual(getCodeLensStateLabel('', ''), codeLensResourceStopped);
    });

    // --- stateStyle is ignored for non-Running/non-Finished states ---

    test('Starting ignores error stateStyle', () => {
        assert.strictEqual(getCodeLensStateLabel(ResourceState.Starting, StateStyle.Error), codeLensResourceStarting);
    });

    test('FailedToStart ignores stateStyle', () => {
        assert.strictEqual(getCodeLensStateLabel(ResourceState.FailedToStart, StateStyle.Warning), codeLensResourceError);
    });
});
