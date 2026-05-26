import * as assert from 'assert';
import * as fs from 'fs';
import * as path from 'path';
import { parse } from 'jsonc-parser';

type LaunchConfiguration = {
    name?: string;
    type?: string;
    preLaunchTask?: string;
};

type LaunchJson = {
    configurations?: LaunchConfiguration[];
};

type Task = {
    label?: string;
    command?: string;
    dependsOn?: string[];
    dependsOrder?: string;
};

type TasksJson = {
    tasks?: Task[];
};

const extensionWorkspaceRoot = path.resolve(__dirname, '../..');
const repositoryRoot = path.resolve(extensionWorkspaceRoot, '..');

function readJsonc<T>(filePath: string): T {
    return parse(fs.readFileSync(filePath, 'utf8')) as T;
}

suite('VS Code extension workspace configuration', () => {
    const workspaceConfigs = [
        { name: 'repository workspace', workspaceRoot: repositoryRoot },
        { name: 'extension workspace', workspaceRoot: extensionWorkspaceRoot },
    ];

    for (const { name, workspaceRoot } of workspaceConfigs) {
        test(`${name} runs yarn install before launching the extension`, () => {
            const launch = readJsonc<LaunchJson>(path.join(workspaceRoot, '.vscode', 'launch.json'));
            const tasks = readJsonc<TasksJson>(path.join(workspaceRoot, '.vscode', 'tasks.json'));

            const extensionLaunches = launch.configurations?.filter(configuration => configuration.type === 'extensionHost') ?? [];
            assert.ok(extensionLaunches.length > 0, 'Expected at least one extension launch configuration');

            for (const configuration of extensionLaunches) {
                assert.strictEqual(
                    configuration.preLaunchTask,
                    'tasks: watch extension',
                    `Expected ${configuration.name} to run the compound watch task before launch.`
                );
            }

            const installTask = tasks.tasks?.find(task => task.label === 'yarn: install extension');
            assert.ok(installTask, 'Expected a yarn install task for the extension.');
            assert.strictEqual(installTask.command, 'yarn install');

            const watchTask = tasks.tasks?.find(task => task.label === 'tasks: watch extension');
            assert.ok(watchTask, 'Expected a compound watch task for extension launches.');
            assert.strictEqual(watchTask.dependsOrder, 'sequence');
            assert.deepStrictEqual(watchTask.dependsOn, [
                'yarn: install extension',
                'npm: watch extension',
            ]);
        });
    }
});
