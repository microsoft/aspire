const extesterModulePath = process.env.ASPIRE_EXTENSION_E2E_EXTESTER_MODULE ?? 'vscode-extension-tester';
const extester = require(extesterModulePath);

export interface WebDriver {
    wait<T>(condition: () => Promise<T | boolean> | T | boolean, timeout?: number, message?: string): Promise<T>;
    executeScript<T>(script: string, ...args: unknown[]): Promise<T>;
    actions(): {
        sendKeys(...keys: string[]): { perform(): Promise<void> };
    };
}

export interface Locator {
}

export interface WebElement {
    getText(): Promise<string>;
}

export interface VSBrowserInstance {
    readonly driver: WebDriver;
    waitForWorkbench(timeout?: number): Promise<void>;
    takeScreenshot(name: string): Promise<void>;
}

export interface ViewItemAction {
    getLabel(): Promise<string>;
    click(): Promise<void>;
}

export interface TreeItem {
    getLabel(): Promise<string>;
    getDescription(): Promise<string | undefined>;
    getTooltip(): Promise<string | undefined>;
    click(): Promise<void>;
    select(): Promise<void>;
    expand(): Promise<void>;
    getChildren(): Promise<TreeItem[]>;
    findChildItem(name: string): Promise<TreeItem | undefined>;
    getActionButton(label: string): Promise<ViewItemAction | undefined>;
    openContextMenu(): Promise<Menu>;
}

export interface TreeSection {
    getTitle(): Promise<string>;
    findItem(label: string, maxLevel?: number): Promise<TreeItem | undefined>;
    getVisibleItems(): Promise<TreeItem[]>;
    openItem(...path: string[]): Promise<TreeItem[]>;
}

export interface SideBarView {
    getContent(): {
        getSection(title: string): Promise<TreeSection>;
        getSections(): Promise<TreeSection[]>;
    };
}

export interface ViewControl {
    openView(): Promise<SideBarView>;
}

export interface ActivityBar {
    getViewControl(name: string): Promise<ViewControl | undefined>;
}

export interface Menu {
    select(...path: string[]): Promise<Menu | undefined>;
    close(): Promise<void>;
}

export interface InputBox {
    setText(text: string): Promise<void>;
    getPlaceHolder(): Promise<string>;
    getTitle(): Promise<string | undefined>;
    getQuickPicks(): Promise<QuickPickItem[]>;
    selectQuickPick(labelOrIndex: string | number): Promise<void>;
    confirm(): Promise<void>;
    cancel(): Promise<void>;
}

export interface QuickPickItem {
    getLabel(): Promise<string>;
    getDescription(): Promise<string | undefined>;
    select(): Promise<void>;
}

export interface Notification {
    getMessage(): Promise<string>;
    dismiss(): Promise<void>;
}

export interface TerminalView {
    getCurrentChannel(): Promise<string>;
    getText(): Promise<string>;
}

export interface EditorView {
    getOpenEditorTitles(): Promise<string[]>;
    closeAllEditors(): Promise<void>;
}

export interface WebView {
    switchToFrame(timeout?: number): Promise<void>;
    switchBack(): Promise<void>;
    findWebElement(locator: Locator): Promise<WebElement>;
}

export interface Workbench {
    executeCommand(command: string): Promise<void>;
    openCommandPrompt(): Promise<InputBox>;
    getNotifications(): Promise<Notification[]>;
}

export const VSBrowser = extester.VSBrowser as { readonly instance: VSBrowserInstance };
export const ActivityBar = extester.ActivityBar as new () => ActivityBar;
export const SideBarView = extester.SideBarView as new () => SideBarView;
export const Workbench = extester.Workbench as new () => Workbench;
export const By = extester.By as { css(selector: string): Locator };
export const InputBox = extester.InputBox as { create(timeout?: number): Promise<InputBox> };
export const BottomBarPanel = extester.BottomBarPanel as new () => { openTerminalView(): Promise<TerminalView> };
export const EditorView = extester.EditorView as new () => EditorView;
export const WebView = extester.WebView as new () => WebView;
