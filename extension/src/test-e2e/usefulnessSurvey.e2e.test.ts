import * as assert from 'assert';
import { waitForExtensionState } from './helpers/assertions';
import { executeE2eControlCommand, reloadWorkspaceForE2E } from './helpers/fixtures';
import { By, VSBrowser } from './helpers/extester';
import { openAspireView, waitForNotificationMessage } from './helpers/vscode';

suite('Usefulness survey E2E', function () {
    this.timeout(180000);

    setup(async () => {
        await openAspireView();
        await waitForExtensionState(file => !!file.extensionHostSessionId, 'survey test bridge activation');
    });

    for (const [label, outcome] of [['Yes', 'yes'], ["Don't ask again", 'never_again']] as const) {
        test(`records ${outcome} locally and suppresses another invitation after reload`, async () => {
            const started = await executeE2eControlCommand({ name: 'probeUsefulnessSurvey', reset: true }, { waitFor: 'started' });
            const notification = await waitForNotificationMessage('Does Aspire improve your development experience?');
            assert.strictEqual(await notification.getMessage(), 'Does Aspire improve your development experience?');
            // ExTester's takeAction interpolates labels into a single-quoted XPath;
            // "Don't ask again" is not escaped there. Match button text without XPath.
            const buttons = await VSBrowser.instance.driver.findElements(By.css('.notification-list-item .monaco-button'));
            let clicked = false;
            for (const button of buttons) {
                if (await button.getText() === label) {
                    await button.click();
                    clicked = true;
                    break;
                }
            }
            assert.strictEqual(clicked, true, `Missing survey action: ${label}`);
            const finished = await waitForExtensionState(
                file => file.control?.revision === started.revision && file.control.status !== 'started',
                'usefulness survey result');
            assert.strictEqual(finished.control?.status, 'applied', JSON.stringify(finished.control));
            assert.deepStrictEqual(finished.control?.result, {
                shown: true, events: [{ kind: 'invitation' }, { kind: 'result', outcome }],
            });
            await reloadWorkspaceForE2E();
            const suppressed = await executeE2eControlCommand({ name: 'probeUsefulnessSurvey', reset: false });
            assert.deepStrictEqual(suppressed.result, { shown: false, events: [] });
        });
    }
});
