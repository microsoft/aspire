import * as assert from 'assert';
import { getCodeLensStateLabel } from '../editor/AspireCodeLensProvider';
import {
    codeLensResourceRunning,
    codeLensResourceRunningWarning,
    codeLensResourceRunningError,
    codeLensResourceStarting,
    codeLensResourceStopping,
    codeLensResourceNotStarted,
    codeLensResourceWaiting,
    codeLensResourceStopped,
    codeLensResourceStoppedWithExitCode,
    codeLensResourceStoppedError,
    codeLensResourceStoppedErrorWithExitCode,
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

    test('Waiting returns waiting label', () => {
        assert.strictEqual(getCodeLensStateLabel(ResourceState.Waiting, ''), codeLensResourceWaiting);
    });

    test('NotStarted returns not-started label', () => {
        assert.strictEqual(getCodeLensStateLabel(ResourceState.NotStarted, ''), codeLensResourceNotStarted);
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

    test('Stopped with no stateStyle returns stopped label', () => {
        assert.strictEqual(getCodeLensStateLabel(ResourceState.Stopped, ''), codeLensResourceStopped);
    });

    test('Finished with error stateStyle returns stopped-error label', () => {
        assert.strictEqual(getCodeLensStateLabel(ResourceState.Finished, StateStyle.Error), codeLensResourceStoppedError);
    });

    test('Exited with error stateStyle returns stopped-error label', () => {
        assert.strictEqual(getCodeLensStateLabel(ResourceState.Exited, StateStyle.Error), codeLensResourceStoppedError);
    });

    test('Stopping returns stopping label', () => {
        assert.strictEqual(getCodeLensStateLabel(ResourceState.Stopping, ''), codeLensResourceStopping);
    });

    test('Stopping with error stateStyle still returns stopping label', () => {
        assert.strictEqual(getCodeLensStateLabel(ResourceState.Stopping, StateStyle.Error), codeLensResourceStopping);
    });

    // --- Exit code tests ---

    test('Finished with exitCode 0 returns stopped label (no exit code shown)', () => {
        assert.strictEqual(getCodeLensStateLabel(ResourceState.Finished, '', 0), codeLensResourceStopped);
    });

    test('Exited with exitCode and error stateStyle returns stopped-error-with-exit-code label', () => {
        assert.strictEqual(getCodeLensStateLabel(ResourceState.Exited, StateStyle.Error, 1), codeLensResourceStoppedErrorWithExitCode(1));
    });

    test('Finished with null exitCode returns stopped label', () => {
        assert.strictEqual(getCodeLensStateLabel(ResourceState.Finished, '', null), codeLensResourceStopped);
    });

    test('Finished with undefined exitCode returns stopped label', () => {
        assert.strictEqual(getCodeLensStateLabel(ResourceState.Finished, ''), codeLensResourceStopped);
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
