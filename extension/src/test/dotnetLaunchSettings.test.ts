import * as assert from 'assert';
import * as path from 'path';
import * as fs from 'fs';
import * as os from 'os';
import { readLaunchSettings } from '../debugger/dotnetLaunchSettings';

suite('Dotnet Launch Settings Tests', () => {
    suite('readLaunchSettings', () => {
        let tempDir: string;
        let projectPath: string;
        let launchSettingsPath: string;

        setup(() => {
            tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'aspire-test-'));
            const projectDir = path.join(tempDir, 'TestProject');
            const propertiesDir = path.join(projectDir, 'Properties');

            fs.mkdirSync(projectDir, { recursive: true });
            fs.mkdirSync(propertiesDir, { recursive: true });

            projectPath = path.join(projectDir, 'TestProject.csproj');
            launchSettingsPath = path.join(propertiesDir, 'launchSettings.json');

            fs.writeFileSync(projectPath, '<Project></Project>');
        });

        teardown(() => {
            if (fs.existsSync(tempDir)) {
                fs.rmSync(tempDir, { recursive: true, force: true });
            }
        });

        test('successfully reads valid launch settings file', async () => {
            const launchSettings = {
                profiles: {
                    'Development': {
                        environmentVariables: {
                            ASPNETCORE_ENVIRONMENT: 'Development'
                        }
                    }
                }
            };

            fs.writeFileSync(launchSettingsPath, JSON.stringify(launchSettings, null, 2));

            const result = await readLaunchSettings(projectPath);

            assert.notStrictEqual(result, null);
            assert.strictEqual(result!.profiles['Development'].environmentVariables!.ASPNETCORE_ENVIRONMENT, 'Development');
        });

        test('returns null when launch settings file does not exist', async () => {
            const result = await readLaunchSettings(projectPath);

            assert.strictEqual(result, null);
        });

        test('returns null when launch settings file has invalid JSON', async () => {
            fs.writeFileSync(launchSettingsPath, '{ invalid json content');

            const result = await readLaunchSettings(projectPath);

            assert.strictEqual(result, null);
        });

        test('handles empty launch settings file', async () => {
            const launchSettings = {
                profiles: {}
            };

            fs.writeFileSync(launchSettingsPath, JSON.stringify(launchSettings));

            const result = await readLaunchSettings(projectPath);

            assert.notStrictEqual(result, null);
            assert.deepStrictEqual(result!.profiles, {});
        });

        test('successfully reads launch settings file with comments', async () => {
            const launchSettingsWithComments = `{
  // This is a comment
  "profiles": {
    /* Multi-line comment
       spanning multiple lines */
    "Development": {
      "commandName": "Project",
      // Comment before environmentVariables
      "environmentVariables": {
        "ASPNETCORE_ENVIRONMENT": "Development", // Inline comment
        "LOG_LEVEL": "Debug" /* Another inline comment */
      },
      // Comment before applicationUrl
      "applicationUrl": "https://localhost:5001",
      "launchBrowser": true
    },
    // Another profile
    "Production": {
      "commandName": "Project",
      "environmentVariables": {
        "ASPNETCORE_ENVIRONMENT": "Production"
      }
    }
  }
}`;

            fs.writeFileSync(launchSettingsPath, launchSettingsWithComments);

            const result = await readLaunchSettings(projectPath);

            assert.notStrictEqual(result, null);
            assert.strictEqual(result!.profiles['Development'].commandName, 'Project');
            assert.strictEqual(result!.profiles['Development'].environmentVariables!.ASPNETCORE_ENVIRONMENT, 'Development');
            assert.strictEqual(result!.profiles['Development'].environmentVariables!.LOG_LEVEL, 'Debug');
            assert.strictEqual(result!.profiles['Development'].applicationUrl, 'https://localhost:5001');
            assert.strictEqual(result!.profiles['Development'].launchBrowser, true);
            assert.strictEqual(result!.profiles['Production'].environmentVariables!.ASPNETCORE_ENVIRONMENT, 'Production');
        });

        test('falls back to aspire.config.json profiles when .run.json does not exist for file-based app', async () => {
            const fileBasedAppPath = path.join(tempDir, 'TestProject', 'apphost.cs');
            fs.writeFileSync(fileBasedAppPath, '// test file-based app');

            const aspireConfigPath = path.join(tempDir, 'TestProject', 'aspire.config.json');
            const aspireConfig = {
                appHost: { path: 'apphost.cs' },
                profiles: {
                    https: {
                        applicationUrl: 'https://localhost:5001;http://localhost:5000',
                        environmentVariables: {
                            ASPNETCORE_ENVIRONMENT: 'Development'
                        }
                    },
                    http: {
                        applicationUrl: 'http://localhost:5000'
                    }
                }
            };
            fs.writeFileSync(aspireConfigPath, JSON.stringify(aspireConfig, null, 2));

            const result = await readLaunchSettings(fileBasedAppPath);

            assert.notStrictEqual(result, null);
            assert.strictEqual(Object.keys(result!.profiles).length, 2);
            assert.strictEqual(result!.profiles['https'].applicationUrl, 'https://localhost:5001;http://localhost:5000');
            assert.strictEqual(result!.profiles['https'].environmentVariables!.ASPNETCORE_ENVIRONMENT, 'Development');
            assert.strictEqual(result!.profiles['https'].commandName, 'Project');
            assert.strictEqual(result!.profiles['http'].applicationUrl, 'http://localhost:5000');
        });

        test('returns null when neither .run.json nor aspire.config.json exists for file-based app', async () => {
            const fileBasedAppPath = path.join(tempDir, 'TestProject', 'apphost.cs');
            fs.writeFileSync(fileBasedAppPath, '// test file-based app');

            const result = await readLaunchSettings(fileBasedAppPath);

            assert.strictEqual(result, null);
        });

        test('prefers .run.json over aspire.config.json profiles for file-based app', async () => {
            const fileBasedAppPath = path.join(tempDir, 'TestProject', 'apphost.cs');
            fs.writeFileSync(fileBasedAppPath, '// test file-based app');

            const runJsonPath = path.join(tempDir, 'TestProject', 'apphost.run.json');
            const runJson = {
                profiles: {
                    default: {
                        commandName: 'Project',
                        applicationUrl: 'https://localhost:7000'
                    }
                }
            };
            fs.writeFileSync(runJsonPath, JSON.stringify(runJson, null, 2));

            const aspireConfigPath = path.join(tempDir, 'TestProject', 'aspire.config.json');
            const aspireConfig = {
                profiles: {
                    default: {
                        applicationUrl: 'https://localhost:9999'
                    }
                }
            };
            fs.writeFileSync(aspireConfigPath, JSON.stringify(aspireConfig, null, 2));

            const result = await readLaunchSettings(fileBasedAppPath);

            assert.notStrictEqual(result, null);
            assert.strictEqual(result!.profiles['default'].applicationUrl, 'https://localhost:7000');
        });

        test('reads aspire.config.json profiles with comments', async () => {
            const fileBasedAppPath = path.join(tempDir, 'TestProject', 'apphost.cs');
            fs.writeFileSync(fileBasedAppPath, '// test file-based app');

            const aspireConfigPath = path.join(tempDir, 'TestProject', 'aspire.config.json');
            const aspireConfigWithComments = `{
  // AppHost configuration
  "appHost": { "path": "apphost.cs" },
  "profiles": {
    "https": {
      "applicationUrl": "https://localhost:5001", // HTTPS endpoint
      "environmentVariables": {
        "ASPNETCORE_ENVIRONMENT": "Development"
      }
    }
  }
}`;
            fs.writeFileSync(aspireConfigPath, aspireConfigWithComments);

            const result = await readLaunchSettings(fileBasedAppPath);

            assert.notStrictEqual(result, null);
            assert.strictEqual(result!.profiles['https'].applicationUrl, 'https://localhost:5001');
            assert.strictEqual(result!.profiles['https'].environmentVariables!.ASPNETCORE_ENVIRONMENT, 'Development');
        });
    });
});
