import { BottomBarPanel, By, EditorView, InputBox, ModalDialog, Notification, SideBarView, TreeItem, TreeSection, VSBrowser, WebView, Workbench } from './extester';
import { error as webDriverError } from 'selenium-webdriver';

const escapeKey = '\uE00C';
const aspireAppHostsSectionTitle = 'AppHosts';
const blockingWebDriverLifecycleErrorNames = new Set([
    'InvalidSessionIdError',
    'NoSuchSessionError',
    'NoSuchWindowError',
    'SessionNotCreatedError',
]);
const blockingWebDriverLifecycleMessageFragments = [
    'session deleted because of page crash',
    'disconnected: not connected to devtools',
    'chrome not reachable',
];

export async function openAspireView(): Promise<TreeSection> {
    let lastSectionTitles: string[] = [];

    for (let attempt = 0; attempt < 3; attempt++) {
        await executeCommandFromPalette('workbench.view.extension.aspire-panel');

        try {
            const section = await VSBrowser.instance.driver.wait(async () => {
                try {
                    const sections = await new SideBarView().getContent().getSections();
                    lastSectionTitles = await Promise.all(sections.map(section => section.getTitle()));
                    const aspireSection = sections.find((_, index) => lastSectionTitles[index] === aspireAppHostsSectionTitle);
                    return aspireSection ?? false;
                }
                catch (error) {
                    throwIfWebDriverSessionFailure(error);
                    return false;
                }
            }, 10000, `Timed out waiting for '${aspireAppHostsSectionTitle}' section.`);

            return section;
        }
        catch (error) {
            throwIfWebDriverSessionFailure(error);
            await delay(250);
        }
    }

    return await VSBrowser.instance.driver.wait(async () => {
        try {
            const sections = await new SideBarView().getContent().getSections();
            lastSectionTitles = await Promise.all(sections.map(section => section.getTitle()));
            const aspireSection = sections.find((_, index) => lastSectionTitles[index] === aspireAppHostsSectionTitle);
            return aspireSection ?? false;
        }
        catch (error) {
            throwIfWebDriverSessionFailure(error);
            return false;
        }
    }, 30000, `Timed out waiting for '${aspireAppHostsSectionTitle}' section. Visible sections: ${lastSectionTitles.join(', ') || '<none>'}.`);
}

/**
 * Switches the side bar away from the Aspire container so the AppHosts view is hidden.
 *
 * The extension keeps its `aspire describe --follow` resource streams alive only while this
 * view is visible, so hiding it puts the repository back into the cold state a window that
 * has never opened the view is in. Tests use it to prove a read-only surface answers from an
 * authoritative read rather than from whatever a stream happened to leave behind.
 */
export async function hideAspireView(timeoutMs = 30000): Promise<void> {
    await executeCommandFromPalette('workbench.view.explorer');
    await VSBrowser.instance.driver.wait(async () => {
        try {
            const sections = await new SideBarView().getContent().getSections();
            const titles = await Promise.all(sections.map(section => section.getTitle()));
            return !titles.includes(aspireAppHostsSectionTitle);
        }
        catch (error) {
            throwIfWebDriverSessionFailure(error);
            return false;
        }
    }, timeoutMs, `Timed out waiting for the '${aspireAppHostsSectionTitle}' section to be hidden.`);
}

export async function observeVisibleSideBarSectionTitles(durationMs = 2000): Promise<string[]> {
    const observedTitles = new Set<string>();
    const deadline = Date.now() + durationMs;

    do {
        const sections = await new SideBarView().getContent().getSections();
        const titles = await Promise.all(sections.map(section => section.getTitle()));
        for (const title of titles) {
            observedTitles.add(title);
        }

        await delay(100);
    } while (Date.now() < deadline);

    return [...observedTitles];
}

export async function waitForTreeItem(section: TreeSection, label: string, timeoutMs = 30000): Promise<TreeItem> {
    return await VSBrowser.instance.driver.wait(async () => {
        try {
            const item = await section.findItem(label, 4);
            if (item) {
                return item;
            }
        }
        catch (error) {
            throwIfWebDriverSessionFailure(error);
        }

        try {
            const sections = await new SideBarView().getContent().getSections();
            const sectionTitles = await Promise.all(sections.map(section => section.getTitle()));
            const currentSection = sections.find((_, index) => sectionTitles[index] === aspireAppHostsSectionTitle);
            return currentSection ? await currentSection.findItem(label, 4) ?? false : false;
        }
        catch (error) {
            throwIfWebDriverSessionFailure(error);
            return false;
        }
    }, timeoutMs, `Timed out waiting for tree item '${label}'.`);
}

export async function waitForChildTreeItem(parent: TreeItem, label: string, timeoutMs = 30000): Promise<TreeItem> {
    return await VSBrowser.instance.driver.wait(async () => {
        try {
            return await parent.findChildItem(label) ?? false;
        }
        catch (error) {
            throwIfWebDriverSessionFailure(error);
            return false;
        }
    }, timeoutMs, `Timed out waiting for child tree item '${label}' on '${await parent.getLabel()}'.`);
}

export async function waitForTreeItemDescription(section: TreeSection, label: string, expectedDescription: string, timeoutMs = 30000): Promise<TreeItem> {
    let lastDescription: string | undefined;

    try {
        return await VSBrowser.instance.driver.wait(async () => {
            try {
                let item = await section.findItem(label, 4);
                if (!item) {
                    const sections = await new SideBarView().getContent().getSections();
                    const sectionTitles = await Promise.all(sections.map(section => section.getTitle()));
                    const currentSection = sections.find((_, index) => sectionTitles[index] === aspireAppHostsSectionTitle);
                    item = currentSection ? await currentSection.findItem(label, 4) : undefined;
                }

                if (!item) {
                    lastDescription = undefined;
                    return false;
                }

                lastDescription = await item.getDescription();
                return lastDescription === expectedDescription ? item : false;
            }
            catch (error) {
                throwIfWebDriverSessionFailure(error);
                return false;
            }
        }, timeoutMs, `Timed out waiting for tree item '${label}' description '${expectedDescription}'.`);
    }
    catch (error) {
        throw withWaitDiagnostics(error, [`Last description for '${label}': ${JSON.stringify(lastDescription)}`]);
    }
}

export async function selectContextMenuItem(item: TreeItem, label: string): Promise<void> {
    const menu = await item.openContextMenu();
    try {
        await menu.select(label);
    }
    finally {
        try {
            await menu.close();
        }
        catch (error) {
            throwIfWebDriverSessionFailure(error);
        }
    }
}

export async function clickTreeItemAction(item: TreeItem, label: string): Promise<void> {
    const action = await item.getActionButton(label);
    if (!action) {
        throw new Error(`Tree item action '${label}' was not found on '${await item.getLabel()}'.`);
    }

    await action.click();
}

export async function clickTreeItem(section: TreeSection, label: string, timeoutMs = 30000): Promise<TreeItem> {
    const item = await waitForTreeItem(section, label, timeoutMs);
    await item.click();
    return item;
}

export async function executeCommandFromPalette(command: string): Promise<void> {
    let lastError: unknown;

    for (let attempt = 0; attempt < 3; attempt++) {
        try {
            await dismissActiveInput();
            await new Workbench().executeCommand(command);
            return;
        }
        catch (error) {
            lastError = error;
            await dismissActiveInput();
            await delay(250);
        }
    }

    throw lastError;
}

export async function reloadWindow(): Promise<void> {
    await dismissActiveInput();
    await new Workbench().executeCommand('Developer: Reload Window');
}

export async function cancelActiveInput(): Promise<void> {
    const input = await VSBrowser.instance.driver.wait(async () => {
        try {
            return await InputBox.create();
        }
        catch (error) {
            throwIfWebDriverSessionFailure(error);
            return false;
        }
    }, 30000, 'Timed out waiting for active input to appear.');
    await input.cancel();
}

export async function answerActiveInput(value: string, expectedPlaceholder: string, timeoutMs = 30000): Promise<void> {
    const input = await waitForActiveInput(expectedPlaceholder, undefined, timeoutMs);
    await input.setText(value);
    await input.confirm();
}

export async function waitForActiveInput(expectedPlaceholder: string, expectedTitle?: string, timeoutMs = 30000): Promise<InputBox> {
    let lastPrompt = '<none>';
    return await VSBrowser.instance.driver.wait(async () => {
        try {
            const candidate = await InputBox.create();
            const placeholder = await candidate.getPlaceHolder();
            const title = await candidate.getTitle();
            lastPrompt = `${title ?? '<no title>'} / ${placeholder}`;
            return placeholder === expectedPlaceholder
                && (expectedTitle === undefined || title === expectedTitle)
                ? candidate
                : false;
        }
        catch (error) {
            throwIfWebDriverSessionFailure(error);
            return false;
        }
    }, timeoutMs, `Timed out waiting for input '${expectedTitle ?? '<any title>'}' / '${expectedPlaceholder}'. Last prompt: ${lastPrompt}.`);
}

export async function answerActiveInputByMessage(value: string, expectedMessage: string, timeoutMs = 30000): Promise<void> {
    let lastMessage = '<none>';
    const input = await VSBrowser.instance.driver.wait(async () => {
        try {
            const widgets = await VSBrowser.instance.driver.findElements(By.css('.quick-input-widget'));
            for (const widget of widgets) {
                if (!await widget.isDisplayed()) {
                    continue;
                }

                const messages = await widget.findElements(By.css('.quick-input-message'));
                lastMessage = (await Promise.all(messages.map(message => message.getText()))).join(' ');
                if (!lastMessage.includes(expectedMessage)) {
                    continue;
                }

                const inputs = await widget.findElements(By.css('.quick-input-box input'));
                for (const candidate of inputs) {
                    if (await candidate.isDisplayed()) {
                        return candidate;
                    }
                }
            }

            return false;
        }
        catch (error) {
            throwIfWebDriverSessionFailure(error);
            return false;
        }
    }, timeoutMs, `Timed out waiting for input message '${expectedMessage}'. Last message: ${lastMessage}.`);
    await input.click();
    await input.sendKeys(value, '\uE007');
}

export async function chooseActiveQuickPick(label: string, timeoutMs = 30000): Promise<void> {
    const input = await VSBrowser.instance.driver.wait(async () => {
        try {
            return await InputBox.create();
        }
        catch (error) {
            throwIfWebDriverSessionFailure(error);
            return false;
        }
    }, timeoutMs, 'Timed out waiting for active quick pick to appear.');
    let visibleLabels: string[] = [];
    const item = await VSBrowser.instance.driver.wait(async () => {
        try {
            const picks = await input.getQuickPicks();
            visibleLabels = await Promise.all(picks.map(pick => pick.getLabel()));
            for (const pick of picks) {
                if (await pick.getLabel() === label) {
                    return pick;
                }
            }

            return false;
        }
        catch (error) {
            throwIfWebDriverSessionFailure(error);
            return false;
        }
    }, timeoutMs, `Timed out waiting for quick pick '${label}'. Visible labels: ${visibleLabels.join(', ') || '<none>'}.`);
    await item.select();
}

export async function chooseActiveQuickPickAtIndex(index: number, timeoutMs = 30000): Promise<void> {
    const input = await VSBrowser.instance.driver.wait(async () => {
        try {
            return await InputBox.create();
        }
        catch (error) {
            throwIfWebDriverSessionFailure(error);
            return false;
        }
    }, timeoutMs, 'Timed out waiting for active quick pick to appear.');
    let visibleLabels: string[] = [];
    const item = await VSBrowser.instance.driver.wait(async () => {
        try {
            const picks = await input.getQuickPicks();
            visibleLabels = await Promise.all(picks.map(pick => pick.getLabel()));
            return picks[index] ?? false;
        }
        catch (error) {
            throwIfWebDriverSessionFailure(error);
            return false;
        }
    }, timeoutMs, `Timed out waiting for quick pick index ${index}. Visible labels: ${visibleLabels.join(', ') || '<none>'}.`);
    await item.select();
}

export async function getActiveQuickPickLabels(timeoutMs = 30000): Promise<string[]> {
    const input = await VSBrowser.instance.driver.wait(async () => {
        try {
            return await InputBox.create();
        }
        catch (error) {
            throwIfWebDriverSessionFailure(error);
            return false;
        }
    }, timeoutMs, 'Timed out waiting for active quick pick to appear.');

    return await VSBrowser.instance.driver.wait(async () => {
        try {
            const picks = await input.getQuickPicks();
            const labels = await Promise.all(picks.map(pick => pick.getLabel()));
            return labels.length > 0 ? labels : false;
        }
        catch (error) {
            throwIfWebDriverSessionFailure(error);
            return false;
        }
    }, timeoutMs, 'Timed out waiting for active quick pick labels.');
}

export async function waitForNotificationMessage(expectedText: string, timeoutMs = 30000): Promise<Notification> {
    return await VSBrowser.instance.driver.wait(async () => {
        try {
            const notifications = await new Workbench().getNotifications();
            for (const notification of notifications) {
                const message = await notification.getMessage();
                if (message.includes(expectedText)) {
                    return notification;
                }
            }

            return false;
        }
        catch (error) {
            // VS Code can replace notification elements while Selenium reads them, so let the
            // next WebDriver poll reacquire the current notification list.
            if (error instanceof webDriverError.StaleElementReferenceError) {
                return false;
            }

            throw error;
        }
    }, timeoutMs, `Timed out waiting for notification containing '${expectedText}'.`);
}

export async function takeNotificationAction(expectedText: string, actionTitle: string, timeoutMs = 30000): Promise<void> {
    await VSBrowser.instance.driver.wait(async () => {
        try {
            const notifications = await new Workbench().getNotifications();
            for (const notification of notifications) {
                if ((await notification.getMessage()).includes(expectedText)) {
                    await notification.takeAction(actionTitle);
                    return true;
                }
            }

            return false;
        }
        catch (error) {
            throwIfWebDriverSessionFailure(error);
            if (error instanceof webDriverError.ElementClickInterceptedError) {
                // VS Code can leave a custom hover over a notification action after Selenium
                // positions the pointer. Escape dismisses that hover so the next poll can click.
                await VSBrowser.instance.driver.actions().sendKeys(escapeKey).perform();
                return false;
            }
            if (error instanceof webDriverError.StaleElementReferenceError
                || error instanceof webDriverError.NoSuchElementError) {
                return false;
            }

            throw error;
        }
    }, timeoutMs, `Timed out selecting notification action '${actionTitle}' from '${expectedText}'.`);
}

export interface ModalDialogInteraction {
    message: string;
    details: string;
}

// Screenshot capture for flows that change UI without a modal dialog. `interactWithModalDialog`
// captures while a dialog is on screen; tools that open UI directly need their own capture point.
export async function captureScreenshot(name: string): Promise<void> {
    await VSBrowser.instance.takeScreenshot(name).catch(() => undefined);
}

export async function interactWithModalDialog(buttonTitle: string, timeoutMs = 120000, screenshotName?: string): Promise<ModalDialogInteraction> {
    let lastError: unknown;
    const interaction = await VSBrowser.instance.driver.wait(async () => {
        try {
            const dialog = new ModalDialog();
            const message = await dialog.getMessage();
            if (!message) {
                return false;
            }

            const details = await dialog.getDetails().catch(() => '');
            if (screenshotName) {
                await VSBrowser.instance.takeScreenshot(screenshotName).catch(() => undefined);
            }

            await dialog.pushButton(buttonTitle);
            return { message, details };
        }
        catch (error) {
            lastError = error;
            return false;
        }
    }, timeoutMs, `Timed out waiting for a modal dialog with a '${buttonTitle}' button. Last error: ${lastError}`);

    return interaction as ModalDialogInteraction;
}

export type AcceptedModalDialog = ModalDialogInteraction;
export const acceptModalDialog = interactWithModalDialog;

export async function getOpenEditorTitles(): Promise<string[]> {
    return await new EditorView().getOpenEditorTitles();
}

export async function waitForOpenEditorCount(minimum: number, timeoutMs = 120000): Promise<string[]> {
    return await VSBrowser.instance.driver.wait(async () => {
        const titles = await getOpenEditorTitles();
        return titles.length >= minimum ? titles : false;
    }, timeoutMs, `Timed out waiting for at least ${minimum} open editor(s).`);
}

export async function isPanelVisible(): Promise<boolean> {
    return (await new BottomBarPanel().getRect()).height > 0;
}

export async function setPanelVisible(visible: boolean, timeoutMs = 30000): Promise<void> {
    await new BottomBarPanel().toggle(visible);
    await waitForPanelVisibility(visible, timeoutMs);
}

export async function waitForPanelVisibility(visible: boolean, timeoutMs = 30000): Promise<void> {
    await VSBrowser.instance.driver.wait(
        async () => await isPanelVisible() === visible,
        timeoutMs,
        `Timed out waiting for the panel to become ${visible ? 'visible' : 'hidden'}.`);
}

export async function getNotificationCount(): Promise<number> {
    return (await new Workbench().getNotifications()).length;
}

export async function getNotificationMessages(): Promise<string[]> {
    const notifications = await new Workbench().getNotifications();
    return await Promise.all(notifications.map(notification => notification.getMessage()));
}

export async function dismissAllNotifications(timeoutMs = 30000): Promise<void> {
    await VSBrowser.instance.driver.wait(async () => {
        try {
            const notifications = await new Workbench().getNotifications();
            if (notifications.length === 0) {
                return true;
            }

            await notifications[0].dismiss();
            return false;
        }
        catch (error) {
            throwIfWebDriverSessionFailure(error);
            if (error instanceof webDriverError.StaleElementReferenceError) {
                return false;
            }

            throw error;
        }
    }, timeoutMs, 'Timed out dismissing VS Code notifications.');
}

export async function waitForNotificationCountGreaterThan(count: number, timeoutMs = 30000): Promise<void> {
    await VSBrowser.instance.driver.wait(async () => {
        const currentCount = await getNotificationCount();
        return currentCount > count;
    }, timeoutMs, `Timed out waiting for notification count to exceed ${count}.`);
}

export async function getCurrentTerminalChannel(): Promise<string> {
    return await (await new BottomBarPanel().openTerminalView()).getCurrentChannel();
}

export async function waitForTerminalChannel(expectedText: string, timeoutMs = 30000): Promise<string> {
    return await VSBrowser.instance.driver.wait(async () => {
        try {
            const channel = await getCurrentTerminalChannel();
            return channel.includes(expectedText) ? channel : false;
        }
        catch (error) {
            throwIfWebDriverSessionFailure(error);
            return false;
        }
    }, timeoutMs, `Timed out waiting for terminal channel containing '${expectedText}'.`);
}

export async function waitForEditorTitle(expectedText: string, timeoutMs = 60000, options?: { matchCase?: boolean }): Promise<string> {
    const expected = options?.matchCase === false ? expectedText.toLowerCase() : expectedText;
    let lastTitles: string[] = [];

    try {
        return await VSBrowser.instance.driver.wait(async () => {
            lastTitles = await new EditorView().getOpenEditorTitles();
            return lastTitles.find(title => options?.matchCase === false ? title.toLowerCase().includes(expected) : title.includes(expected)) ?? false;
        }, timeoutMs, `Timed out waiting for editor title containing '${expectedText}'.`);
    }
    catch (error) {
        throw withWaitDiagnostics(error, [`Open editor titles: ${formatDiagnosticList(lastTitles)}`]);
    }
}

/**
 * Waits for a CodeLens whose text contains <paramref name="expectedText"/> in the named editor.
 *
 * The widget spans are read directly rather than through `TextEditor.getCodeLenses()` because that
 * API enumerates `.//span[contains(@widgetid, 'codelens.widget')]/a[@id]` -- only the *clickable*
 * lenses. A lens contributed with an empty command id is rendered by VS Code as plain text rather
 * than a link, so it has no anchor element and is structurally invisible to that API. Aspire's
 * entry point warnings are exactly that shape: they state a fact and have nothing to navigate to.
 *
 * One widget exists per line and holds every lens on it, so the returned strings are per line and
 * read like the editor does, e.g. `Run | Debug | ⚠️ Do not click the Java Run or Debug actions...`.
 */
export async function waitForCodeLensText(fileName: string, expectedText: string, timeoutMs = 60000): Promise<string[]> {
    let lastTexts: string[] = [];

    try {
        return await VSBrowser.instance.driver.wait(async () => {
            try {
                await new EditorView().openEditor(fileName);
                lastTexts = await VSBrowser.instance.driver.executeScript<string[]>(
                    `return Array.from(document.querySelectorAll('[widgetid*="codelens.widget"]')).map(widget => widget.innerText || widget.textContent || '');`);
            }
            catch (error) {
                throwIfWebDriverSessionFailure(error);
                return false;
            }

            return lastTexts.some(text => text.includes(expectedText)) ? lastTexts : false;
        }, timeoutMs, `Timed out waiting for a CodeLens containing '${expectedText}' in '${fileName}'.`);
    }
    catch (error) {
        throw withWaitDiagnostics(error, [`CodeLenses: ${formatDiagnosticList(lastTexts)}`]);
    }
}

export async function waitForWorkbenchText(expectedText: string, timeoutMs = 30000): Promise<string> {
    let lastText = '';

    try {
        return await VSBrowser.instance.driver.wait(async () => {
            lastText = await getWorkbenchAndWebviewText();
            return lastText.includes(expectedText) ? lastText : false;
        }, timeoutMs, `Timed out waiting for workbench text containing '${expectedText}'.`);
    }
    catch (error) {
        throw withWaitDiagnostics(error, [`Last workbench/webview text (${lastText.length} chars):\n${truncateDiagnosticText(lastText)}`]);
    }
}

export async function waitForAnyWorkbenchText(expectedTexts: readonly string[], timeoutMs = 30000): Promise<string> {
    let lastText = '';
    const expectedTextDescription = expectedTexts.map(text => `'${text}'`).join(' or ');

    try {
        return await VSBrowser.instance.driver.wait(async () => {
            lastText = await getWorkbenchAndWebviewText();
            return expectedTexts.some(text => lastText.includes(text)) ? lastText : false;
        }, timeoutMs, `Timed out waiting for workbench text containing ${expectedTextDescription}.`);
    }
    catch (error) {
        throw withWaitDiagnostics(error, [`Last workbench/webview text (${lastText.length} chars):\n${truncateDiagnosticText(lastText)}`]);
    }
}

const appHostsSectionTransitionStateKey = '__aspireAppHostsSectionTransition';

export async function startAppHostsSectionTextTransition(expectedTexts: readonly string[], expectedPattern: RegExp, timeoutMs = 30000): Promise<void> {
    await VSBrowser.instance.driver.wait(async () => {
        return await VSBrowser.instance.driver.executeScript<boolean>(`
            const [stateKey, sectionTitle, expectedTexts, patternSource, patternFlags] = arguments;
            const titles = Array.from(document.querySelectorAll('.part.sidebar .pane > .pane-header > .title'));
            const title = titles.find(candidate => candidate.textContent?.trim() === sectionTitle);
            const pane = title?.closest('.pane');
            if (!pane || pane.getClientRects().length === 0) {
                return false;
            }

            const expectedPattern = new RegExp(patternSource, patternFlags);
            const matchesExpectedText = element => {
                expectedPattern.lastIndex = 0;
                return element.getClientRects().length > 0
                    && expectedTexts.every(text => element.innerText.includes(text))
                    && expectedPattern.test(element.innerText);
            };
            const trackedRow = Array.from(pane.querySelectorAll('.monaco-list-row')).find(matchesExpectedText);
            if (!trackedRow) {
                return false;
            }

            window[stateKey]?.observer?.disconnect();
            const state = { lastNonMatchingAt: 0, trackedRow };
            state.observer = new MutationObserver(records => {
                const sawNonMatchingText = records.some(record =>
                    Array.from(record.removedNodes).some(node => node === trackedRow || node.contains?.(trackedRow)))
                    || !trackedRow.isConnected
                    || !matchesExpectedText(trackedRow);
                if (sawNonMatchingText) {
                    state.lastNonMatchingAt = Date.now();
                }
            });
            state.observer.observe(pane, { childList: true, subtree: true, characterData: true });
            window[stateKey] = state;
            return true;
        `, appHostsSectionTransitionStateKey, aspireAppHostsSectionTitle, expectedTexts, expectedPattern.source, expectedPattern.flags);
    }, timeoutMs, `Timed out starting '${aspireAppHostsSectionTitle}' section text transition tracking.`);
}

export async function cancelAppHostsSectionTextTransition(): Promise<void> {
    await VSBrowser.instance.driver.executeScript(`
        window[arguments[0]]?.observer?.disconnect();
        delete window[arguments[0]];
    `, appHostsSectionTransitionStateKey).catch(() => undefined);
}

export async function waitForAppHostsSectionTextAfterTransition(expectedTexts: readonly string[], expectedPattern: RegExp, notBeforeTimestamp: number, timeoutMs = 30000): Promise<string> {
    let lastText = '';
    const expectedDescription = [...expectedTexts.map(text => `'${text}'`), expectedPattern.toString()].join(' and ');

    try {
        return await VSBrowser.instance.driver.wait(async () => {
            const result = await VSBrowser.instance.driver.executeScript<{ matched: boolean; text: string }>(`
                const [stateKey, sectionTitle, expectedTexts, patternSource, patternFlags, notBeforeTimestamp] = arguments;
                return new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(() => {
                    const state = window[stateKey];
                    const titles = Array.from(document.querySelectorAll('.part.sidebar .pane > .pane-header > .title'));
                    const title = titles.find(candidate => candidate.textContent?.trim() === sectionTitle);
                    const pane = title?.closest('.pane');
                    const text = pane && pane.getClientRects().length > 0 ? pane.innerText : '';
                    const expectedPattern = new RegExp(patternSource, patternFlags);
                    const matchesExpectedText = row => {
                        expectedPattern.lastIndex = 0;
                        return row.getClientRects().length > 0
                            && expectedTexts.every(expectedText => row.innerText.includes(expectedText))
                            && expectedPattern.test(row.innerText);
                    };
                    const matchingRow = pane
                        ? Array.from(pane.querySelectorAll('.monaco-list-row')).find(matchesExpectedText)
                        : undefined;
                    const matched = Boolean(state?.lastNonMatchingAt >= notBeforeTimestamp && matchingRow);
                    if (matched) {
                        state.observer.disconnect();
                        delete window[stateKey];
                    }
                    resolve({ matched, text });
                })));
            `, appHostsSectionTransitionStateKey, aspireAppHostsSectionTitle, expectedTexts, expectedPattern.source, expectedPattern.flags, notBeforeTimestamp);
            lastText = result.text;
            return result.matched ? lastText : false;
        }, timeoutMs, `Timed out waiting for '${aspireAppHostsSectionTitle}' section text to transition back to ${expectedDescription}.`);
    }
    catch (error) {
        await VSBrowser.instance.driver.executeScript(`
            window[arguments[0]]?.observer?.disconnect();
            delete window[arguments[0]];
        `, appHostsSectionTransitionStateKey).catch(() => undefined);
        throw withWaitDiagnostics(error, [`Last '${aspireAppHostsSectionTitle}' section text (${lastText.length} chars):\n${truncateDiagnosticText(lastText)}`]);
    }
}

export async function waitForWorkbenchTextAfterIntegratedBrowserNavigation(expectedText: string | readonly string[], timeoutMs = 120000): Promise<string> {
    const expectedTexts = Array.isArray(expectedText) ? expectedText : [expectedText];
    const expectedTextDescription = expectedTexts.map(text => `'${text}'`).join(' or ');
    let lastReload = 0;
    let reloadCount = 0;
    let lastText = '';

    try {
        return await VSBrowser.instance.driver.wait(async () => {
            lastText = await getWorkbenchAndWebviewText();
            if (expectedTexts.some(text => lastText.includes(text))) {
                return lastText;
            }

            if ((lastText.includes('Failed to Load Page') || lastText.includes('ERR_CONNECTION_REFUSED')) && Date.now() - lastReload > 5000) {
                lastReload = Date.now();
                reloadCount++;
                // VS Code's integrated browser can navigate as soon as the extension receives
                // a healthy dashboard URL, before Chromium has a successful connection open.
                await executeCommandFromPalette('workbench.action.webview.reloadWebview');
            }

            return false;
        }, timeoutMs, `Timed out waiting for integrated browser text containing ${expectedTextDescription}.`);
    }
    catch (error) {
        throw withWaitDiagnostics(error, [
            `Integrated browser reload attempts: ${reloadCount}`,
            `Last workbench/webview text (${lastText.length} chars):\n${truncateDiagnosticText(lastText)}`
        ]);
    }
}

export async function closeAllEditors(): Promise<void> {
    await new EditorView().closeAllEditors();
}

async function getWorkbenchAndWebviewText(): Promise<string> {
    const driver = VSBrowser.instance.driver;
    const outerText = await driver.executeScript<string>('return document.body?.innerText ?? "";');
    const webview = new WebView();

    try {
        await webview.switchToFrame(1000);
        const webviewText = await (await webview.findWebElement(By.css('body'))).getText();
        return `${outerText}\n${webviewText}`;
    }
    catch (error) {
        throwIfWebDriverSessionFailure(error);
        return outerText;
    }
    finally {
        try {
            await webview.switchBack();
        }
        catch (error) {
            throwIfWebDriverSessionFailure(error);
        }
    }
}

function withWaitDiagnostics(error: unknown, diagnostics: string[]): Error {
    const originalMessage = error instanceof Error ? error.message : String(error);
    const enrichedError = new Error(`${originalMessage}\n\n${diagnostics.join('\n')}`);

    if (error instanceof Error) {
        enrichedError.name = error.name;
        if (error.stack) {
            enrichedError.stack = `${enrichedError.message}\nCaused by: ${error.stack}`;
        }
    }

    return enrichedError;
}

function throwIfWebDriverSessionFailure(error: unknown): void {
    if (!(error instanceof Error)) {
        return;
    }

    if (blockingWebDriverLifecycleErrorNames.has(error.name)) {
        throw error;
    }

    // Selenium uses WebDriverError for both transient failures and browser lifecycle failures:
    //   unknown error: session deleted because of page crash
    //   unknown error: disconnected: not connected to DevTools
    //   unknown error: chrome not reachable
    const message = error.message.toLowerCase();
    if (error.name === 'WebDriverError' &&
        blockingWebDriverLifecycleMessageFragments.some(fragment => message.includes(fragment))) {
        throw error;
    }
}

function formatDiagnosticList(values: string[]): string {
    return values.length === 0 ? '(none)' : values.map(value => JSON.stringify(value)).join(', ');
}

function truncateDiagnosticText(text: string, maxLength = 4000): string {
    if (text.length <= maxLength) {
        return text || '(empty)';
    }

    return `${text.slice(0, maxLength)}\n... truncated ${text.length - maxLength} chars`;
}

async function dismissActiveInput(): Promise<void> {
    await VSBrowser.instance.driver.actions().sendKeys(escapeKey).perform();
}

function delay(ms: number): Promise<void> {
    return new Promise(resolve => setTimeout(resolve, ms));
}
