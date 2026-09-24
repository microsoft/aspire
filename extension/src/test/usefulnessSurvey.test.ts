import * as assert from 'assert';
import { testSurveyCampaign, TestUsefulnessSurvey } from './helpers/usefulnessSurvey';

suite('Usefulness survey', () => {
    let h: TestUsefulnessSurvey;
    const shownKey = 'aspire.usefulnessSurvey.shown';
    setup(() => { h = new TestUsefulnessSurvey(); });
    teardown(() => h.dispose());

    test('first Aspire action qualifies after exactly two quiet minutes', async () => {
        h.service.recordCommand('aspire-vscode.runAppHost');
        await h.clock.tickAsync(119_999);
        assert.strictEqual(h.shown, 0);
        assert.deepStrictEqual(h.persistence.writes, []);
        await h.clock.tickAsync(1);
        assert.strictEqual(h.shown, 1);
        assert.deepStrictEqual(h.events, [
            { kind: 'invitation', outcome: undefined }, { kind: 'result', outcome: 'yes' },
        ]);
    });

    test('ignores activation-equivalent and unrelated commands', async () => {
        for (const command of ['other.runAppHost', 'aspire-vscode.settings', 'aspire-vscode.refreshAppHosts', 'aspire-vscode.copyAppHostPath']) {
            h.service.recordCommand(command);
        }
        await h.show();
        assert.strictEqual(h.shown, 0);
        assert.deepStrictEqual(h.persistence.writes, []);
    });

    for (const command of ['new', 'init', 'add', 'update', 'updateSelf']) {
        test(`${command} terminal dispatch does not qualify and cancels a pending invitation`, async () => {
            h.service.recordCommand(`aspire-vscode.${command}`);
            await h.show();
            assert.strictEqual(h.shown, 0);
            h.service.recordCommand('aspire-vscode.runAppHost');
            await h.clock.tickAsync(119_999);
            h.service.recordCommand(`aspire-vscode.${command}`);
            await h.show();
            assert.strictEqual(h.shown, 0);
            assert.deepStrictEqual(h.persistence.writes, []);
        });
    }

    for (const command of [
        'stopAppHost', 'codeLensDebugPipelineStep', 'codeLensResourceAction',
        'codeLensViewLogs', 'codeLensOpenDashboard', 'codeLensViewAppHostLogs',
    ]) {
        test(`${command} qualifies after the same quiet delay as tree actions`, async () => {
            h.service.recordCommand(`aspire-vscode.${command}`);
            await h.clock.tickAsync(119_999);
            assert.strictEqual(h.shown, 0);
            await h.clock.tickAsync(1);
            assert.strictEqual(h.shown, 1);
        });
    }

    for (const outcome of ['yes', 'no', 'dismissed', 'never_again'] as const) {
        test(`${outcome} retires the prompt across reload and campaign changes`, async () => {
            h.response = outcome;
            h.service.recordCommand('aspire-vscode.runAppHost');
            await h.show();
            assert.strictEqual(h.events[1].outcome, outcome);
            assert.deepStrictEqual([...h.persistence.values], [[shownKey, true]]);
            h.service.dispose();
            h.service = h.createService({ ...testSurveyCampaign, id: 'test-v2' });
            h.service.recordCommand('aspire-vscode.runAppHost');
            await h.show();
            assert.strictEqual(h.shown, 1);
            assert.deepStrictEqual(h.persistence.writes, [shownKey]);
        });
    }

    test('further activity resets the quiet delay', async () => {
        h.service.recordCommand('aspire-vscode.runAppHost');
        await h.clock.tickAsync(119_999);
        h.service.recordCommand('aspire-vscode.viewResourceLogs');
        await h.clock.tickAsync(1);
        assert.strictEqual(h.shown, 0);
        await h.clock.tickAsync(119_999);
        assert.strictEqual(h.shown, 1);
    });

    test('opt-out cancels pending display without storing activity', async () => {
        h.service.recordCommand('aspire-vscode.runAppHost');
        h.allowed = false;
        h.service.permissionsChanged();
        await h.show();
        h.allowed = true;
        h.service.permissionsChanged();
        await h.show();
        assert.strictEqual(h.shown, 0);
        assert.deepStrictEqual(h.persistence.writes, []);
        h.service.recordCommand('aspire-vscode.runAppHost');
        await h.show();
        assert.strictEqual(h.shown, 1);
    });

    for (const outcome of ['yes', 'never_again'] as const) {
        test(`consent changes while open suppress ${outcome} telemetry but retain local suppression`, async () => {
            h.holdResponse = true;
            h.service.recordCommand('aspire-vscode.runAppHost');
            await h.show();
            h.allowed = false;
            h.service.permissionsChanged();
            h.allowed = true;
            h.service.permissionsChanged();
            h.answer!(outcome);
            await h.clock.tickAsync(0);
            assert.deepStrictEqual(h.events, [{ kind: 'invitation', outcome: undefined }]);
            assert.deepStrictEqual([...h.persistence.values], [[shownKey, true]]);
        });
    }

    test('an unfocused or busy window requires a fresh action rather than a delayed retry', async () => {
        h.service.recordCommand('aspire-vscode.runAppHost');
        h.focused = false;
        await h.show();
        h.focused = true;
        await h.clock.tickAsync(60 * 60 * 1000);
        assert.strictEqual(h.shown, 0);
        h.service.recordCommand('aspire-vscode.runAppHost');
        await h.show();
        assert.strictEqual(h.shown, 1);
    });

    test('rechecks suppression written by another window before display', async () => {
        h.service.recordCommand('aspire-vscode.runAppHost');
        await h.persistence.update(shownKey, true);
        await h.show();
        assert.strictEqual(h.shown, 0);
        assert.deepStrictEqual(h.persistence.writes, [shownKey]);
    });

    test('disposal cancels a timer and invalidates an open answer', async () => {
        h.service.recordCommand('aspire-vscode.runAppHost');
        h.service.dispose();
        await h.show();
        assert.strictEqual(h.shown, 0);
        h.service = h.createService(testSurveyCampaign);
        h.holdResponse = true;
        h.service.recordCommand('aspire-vscode.runAppHost');
        await h.show();
        h.service.dispose();
        h.answer!('yes');
        await h.clock.tickAsync(0);
        assert.deepStrictEqual(h.events, [{ kind: 'invitation', outcome: undefined }]);
    });

    test('disabled, expired, and opted-out sessions never prompt or write', async () => {
        for (const campaign of [
            { ...testSurveyCampaign, enabled: false },
            { ...testSurveyCampaign, expiresAt: Date.now() },
        ]) {
            h.service.dispose();
            h.service = h.createService(campaign);
            h.service.recordCommand('aspire-vscode.runAppHost');
            await h.show();
        }
        h.service.dispose();
        h.service = h.createService(testSurveyCampaign);
        h.allowed = false;
        h.service.recordCommand('aspire-vscode.runAppHost');
        await h.show();
        assert.strictEqual(h.shown, 0);
        assert.deepStrictEqual(h.persistence.writes, []);
    });

    test('failed persistence disables the survey instead of showing an unsuppressed prompt', async () => {
        h.service.recordCommand('aspire-vscode.runAppHost');
        h.persistence.beforeWrite = () => { throw new Error('Test write failure.'); };
        await h.show();
        assert.strictEqual(h.warnings, 1);
        assert.strictEqual(h.shown, 0);
        h.persistence.beforeWrite = undefined;
        h.service.recordCommand('aspire-vscode.runAppHost');
        await h.show();
        assert.strictEqual(h.shown, 0);
        assert.deepStrictEqual(h.persistence.writes, []);
    });

    test('invalid suppression state fails closed without being overwritten', async () => {
        await h.persistence.update(shownKey, 'invalid');
        h.service.recordCommand('aspire-vscode.runAppHost');
        await h.show();
        assert.strictEqual(h.warnings, 1);
        assert.strictEqual(h.shown, 0);
        assert.strictEqual(h.persistence.get(shownKey), 'invalid');
    });

    for (const gate of ['focus', 'consent'] as const) {
        test(`loss of ${gate} while persisting suppression prevents display`, async () => {
            h.persistence.beforeWrite = () => {
                if (gate === 'focus') {
                    h.focused = false;
                }
                else {
                    h.allowed = false;
                    h.service.permissionsChanged();
                }
            };
            h.service.recordCommand('aspire-vscode.runAppHost');
            await h.show();
            assert.strictEqual(h.shown, 0);
            assert.deepStrictEqual([...h.persistence.values], [[shownKey, true]]);
        });
    }
});
