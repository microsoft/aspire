import * as path from 'path';
import * as fs from 'fs';
import { extensionLogOutputChannel } from '../utils/logging';
import { isFileBasedApp } from './languages/dotnet';
import { stripComments } from 'jsonc-parser';
import { aspireConfigFileName, AspireConfigProfile } from '../utils/cliTypes';

/*
 * Represents the subset of a .NET launchSettings.json profile that the VS Code extension consumes.
 */
export interface LaunchProfile {
    commandName: string;
    executablePath?: string;
    workingDirectory?: string;
    commandLineArgs?: string;
    launchBrowser?: boolean;
    applicationUrl?: string;
    environmentVariables?: { [key: string]: string };
    useSSL?: boolean;
}

export interface LaunchSettings {
    profiles: { [key: string]: LaunchProfile };
}

/**
 * Reads and parses the launchSettings.json file for a given project.
 */
export async function readLaunchSettings(projectPath: string): Promise<LaunchSettings | null> {
    try {
        let launchSettingsPath: string;

        if (isFileBasedApp(projectPath)) {
            const fileNameWithoutExt = path.basename(projectPath, path.extname(projectPath));
            launchSettingsPath = path.join(path.dirname(projectPath), `${fileNameWithoutExt}.run.json`);
        } else {
            const projectDir = path.dirname(projectPath);
            launchSettingsPath = path.join(projectDir, 'Properties', 'launchSettings.json');
        }

        const launchSettingsExists = fs.existsSync(launchSettingsPath);
        extensionLogOutputChannel.debug('[launchSettings] Resolved launchSettings path', {
            projectPath,
            resolvedPath: launchSettingsPath,
            exists: launchSettingsExists,
        });

        if (launchSettingsExists) {
            let content = fs.readFileSync(launchSettingsPath, 'utf8');
            content = stripComments(content);
            const launchSettings = JSON.parse(content) as LaunchSettings;

            const profileNames = launchSettings?.profiles ? Object.keys(launchSettings.profiles) : [];
            extensionLogOutputChannel.debug(`[launchSettings] parsed ${profileNames.length} profiles: ${profileNames.join(', ')}`);

            extensionLogOutputChannel.debug(`Successfully read launch settings from: ${launchSettingsPath}`);
            return launchSettings;
        }

        extensionLogOutputChannel.debug(`Launch settings file not found at: ${launchSettingsPath}`);

        const aspireConfigPath = path.join(path.dirname(projectPath), aspireConfigFileName);
        if (fs.existsSync(aspireConfigPath)) {
            let content = fs.readFileSync(aspireConfigPath, 'utf8');
            content = stripComments(content);
            const aspireConfig = JSON.parse(content);

            if (aspireConfig?.profiles && typeof aspireConfig.profiles === 'object') {
                const profiles: { [key: string]: LaunchProfile } = {};
                for (const [name, profile] of Object.entries(aspireConfig.profiles)) {
                    const launchProfile = profile as AspireConfigProfile;
                    profiles[name] = {
                        commandName: 'Project',
                        applicationUrl: launchProfile.applicationUrl,
                        environmentVariables: launchProfile.environmentVariables,
                    };
                }

                extensionLogOutputChannel.debug(`Successfully read launch profiles from: ${aspireConfigPath}`);
                return { profiles };
            }
        }

        return null;
    } catch (error) {
        extensionLogOutputChannel.error(`Failed to read launch settings for project ${projectPath}: ${error}`);
        return null;
    }
}
