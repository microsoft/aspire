import { BottomBarPanel, By, EditorView, InputBox, Notification, SideBarView, TreeItem, TreeSection, VSBrowser, WebView, Workbench } from './extester';

const escapeKey = '\uE00C';
const aspireAppHostsSectionTitle = 'AppHosts';

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
                catch {
                    return false;
                }
            }, 10000, `Timed out waiting for '${aspireAppHostsSectionTitle}' section.`);

            return section;
        }
        catch {
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
        catch {
            return false;
        }
    }, 30000, `Timed out waiting for '${aspireAppHostsSectionTitle}' section. Visible sections: ${lastSectionTitles.join(', ') || '<none>'}.`);
}

export async function waitForTreeItem(section: TreeSection, label: string, timeoutMs = 30000): Promise<TreeItem> {
    return await VSBrowser.instance.driver.wait(async () => {
        try {
            const item = await section.findItem(label, 4);
            if (item) {
                return item;
            }
        }
        catch {
        }

        try {
            const sections = await new SideBarView().getContent().getSections();
            const sectionTitles = await Promise.all(sections.map(section => section.getTitle()));
            const currentSection = sections.find((_, index) => sectionTitles[index] === aspireAppHostsSectionTitle);
            return currentSection ? await currentSection.findItem(label, 4) ?? false : false;
        }
        catch {
            return false;
        }
    }, timeoutMs, `Timed out waiting for tree item '${label}'.`);
}

export async function waitForChildTreeItem(parent: TreeItem, label: string, timeoutMs = 30000): Promise<TreeItem> {
    return await VSBrowser.instance.driver.wait(async () => {
        try {
            return await parent.findChildItem(label) ?? false;
        }
        catch {
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
            catch {
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
        catch {
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

export async function cancelActiveInput(): Promise<void> {
    const input = await VSBrowser.instance.driver.wait(async () => {
        try {
            return await InputBox.create();
        }
        catch {
            return false;
        }
    }, 30000, 'Timed out waiting for active input to appear.');
    await input.cancel();
}

export async function answerActiveInput(value: string, expectedPlaceholder: string, timeoutMs = 30000): Promise<void> {
    let lastPrompt = '<none>';
    const input = await VSBrowser.instance.driver.wait(async () => {
        try {
            const candidate = await InputBox.create();
            const placeholder = await candidate.getPlaceHolder();
            const title = await candidate.getTitle();
            lastPrompt = `${title ?? '<no title>'} / ${placeholder}`;
            return placeholder === expectedPlaceholder ? candidate : false;
        }
        catch {
            return false;
        }
    }, timeoutMs, `Timed out waiting for input placeholder '${expectedPlaceholder}'. Last prompt: ${lastPrompt}.`);
    await input.setText(value);
    await input.confirm();
}

export async function chooseActiveQuickPick(label: string, timeoutMs = 30000): Promise<void> {
    const input = await VSBrowser.instance.driver.wait(async () => {
        try {
            return await InputBox.create();
        }
        catch {
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
        catch {
            return false;
        }
    }, timeoutMs, `Timed out waiting for quick pick '${label}'. Visible labels: ${visibleLabels.join(', ') || '<none>'}.`);
    await item.select();
}

export async function getActiveQuickPickLabels(timeoutMs = 30000): Promise<string[]> {
    const input = await VSBrowser.instance.driver.wait(async () => {
        try {
            return await InputBox.create();
        }
        catch {
            return false;
        }
    }, timeoutMs, 'Timed out waiting for active quick pick to appear.');

    return await VSBrowser.instance.driver.wait(async () => {
        try {
            const picks = await input.getQuickPicks();
            const labels = await Promise.all(picks.map(pick => pick.getLabel()));
            return labels.length > 0 ? labels : false;
        }
        catch {
            return false;
        }
    }, timeoutMs, 'Timed out waiting for active quick pick labels.');
}

export async function waitForNotificationMessage(expectedText: string, timeoutMs = 30000): Promise<Notification> {
    return await VSBrowser.instance.driver.wait(async () => {
        const notifications = await new Workbench().getNotifications();
        for (const notification of notifications) {
            const message = await notification.getMessage();
            if (message.includes(expectedText)) {
                return notification;
            }
        }

        return false;
    }, timeoutMs, `Timed out waiting for notification containing '${expectedText}'.`);
}

export async function getNotificationCount(): Promise<number> {
    return (await new Workbench().getNotifications()).length;
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
        catch {
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
    catch {
        return outerText;
    }
    finally {
        try {
            await webview.switchBack();
        }
        catch {
        }
    }
}

function withWaitDiagnostics(error: unknown, diagnostics: string[]): Error {
    const originalMessage = error instanceof Error ? error.message : String(error);
    const enrichedError = new Error(`${originalMessage}\n\n${diagnostics.join('\n')}`);

    if (error instanceof Error && error.stack) {
        enrichedError.stack = `${enrichedError.message}\nCaused by: ${error.stack}`;
    }

    return enrichedError;
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
