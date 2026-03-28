import * as assert from 'assert';
import * as fs from 'fs';
import * as path from 'path';

type ManifestMenuItem = {
    command?: string;
    when?: string;
};

type ExtensionManifest = {
    contributes: {
        viewsWelcome?: Array<{ view?: string; contents?: string; when?: string }>;
        menus?: {
            'view/title'?: ManifestMenuItem[];
        };
    };
};

function readManifest(): ExtensionManifest {
    const manifestPath = path.resolve(__dirname, '../../package.json');
    return JSON.parse(fs.readFileSync(manifestPath, 'utf8')) as ExtensionManifest;
}

suite('extension/package.json', () => {
    test('running apphosts welcome states use string view mode checks', () => {
        const manifest = readManifest();
        const runningAppHostsWelcome = manifest.contributes.viewsWelcome?.filter(item => item.view === 'aspire-vscode.runningAppHosts') ?? [];

        const workspaceWelcome = runningAppHostsWelcome.find(item => item.contents === '%views.runningAppHosts.welcome%');
        const globalWelcome = runningAppHostsWelcome.find(item => item.contents === '%views.runningAppHosts.globalWelcome%');

        assert.strictEqual(
            workspaceWelcome?.when,
            "aspire.noRunningAppHosts && !aspire.fetchAppHostsError && !aspire.loading && aspire.viewMode != 'global'"
        );
        assert.strictEqual(
            globalWelcome?.when,
            "aspire.noRunningAppHosts && !aspire.fetchAppHostsError && !aspire.loading && aspire.viewMode == 'global'"
        );
    });

    test('running apphosts title actions use string view mode checks', () => {
        const manifest = readManifest();
        const titleMenus = manifest.contributes.menus?.['view/title'] ?? [];

        const switchToGlobal = titleMenus.find(item => item.command === 'aspire-vscode.switchToGlobalView');
        const switchToWorkspace = titleMenus.find(item => item.command === 'aspire-vscode.switchToWorkspaceView');

        assert.strictEqual(
            switchToGlobal?.when,
            "view == aspire-vscode.runningAppHosts && aspire.viewMode != 'global'"
        );
        assert.strictEqual(
            switchToWorkspace?.when,
            "view == aspire-vscode.runningAppHosts && aspire.viewMode == 'global'"
        );
    });
});
