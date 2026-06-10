/**
 * Shared type definitions for Aspire configuration information.
 * These types are used across multiple files to avoid duplication.
 *
 * IMPORTANT: property names are camelCase to match the CLI's JSON output. The CLI serializes
 * `aspire config info --json` with a camelCase naming policy (see JsonSourceGenerationContext in
 * src/Aspire.Cli), so these interfaces MUST use camelCase to read the payload correctly.
 */

export interface FeatureInfo {
    name: string;
    description: string;
    defaultValue: boolean;
}

export interface PropertyInfo {
    name: string;
    type: string;
    description: string;
    required: boolean;
    subProperties?: PropertyInfo[];
    additionalPropertiesType?: string;
}

export interface SettingsSchema {
    properties: PropertyInfo[];
}

export interface ConfigInfo {
    localSettingsPath: string;
    globalSettingsPath: string;
    availableFeatures: FeatureInfo[];
    localSettingsSchema: SettingsSchema;
    globalSettingsSchema: SettingsSchema;
    configFileSchema?: SettingsSchema;
    capabilities?: string[];
}

/**
 * Capability advertised by the CLI when `aspire describe` supports the hidden
 * `--include-disabled-commands` flag. Tooling uses this to avoid passing the flag to older CLIs
 * that don't understand it (which would otherwise produce no resource data). Keep in sync with
 * `KnownCapabilities.DescribeIncludeDisabledCommands` in src/Aspire.Cli/Utils/ExtensionHelper.cs.
 */
export const describeIncludeDisabledCommandsCapability = 'describe-include-disabled-commands.v1';
