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

export type CapabilityStatus = 'supported' | 'unsupported' | 'unavailable';

/**
 * Capability advertised by the CLI when interaction-service pipeline actions are available.
 * Keep in sync with `KnownCapabilities.Pipelines` in src/Aspire.Cli/Utils/ExtensionHelper.cs.
 */
export const pipelineInteractionCapability = 'pipelines';

/**
 * Capability advertised by the CLI when `aspire do --list-steps --format json` returns pipeline
 * metadata without executing the pipeline. Keep in sync with `KnownCapabilities.PipelineStepListJson`
 * in src/Aspire.Cli/Utils/ExtensionHelper.cs.
 */
export const pipelineStepListJsonCapability = 'pipeline-step-list-json.v1';

/**
 * Capability advertised by the CLI when `aspire describe` supports the hidden
 * `--include-disabled-commands` flag. Tooling uses this to avoid passing the flag to older CLIs
 * that don't understand it (which would otherwise produce no resource data). Keep in sync with
 * `KnownCapabilities.DescribeIncludeDisabledCommands` in src/Aspire.Cli/Utils/ExtensionHelper.cs.
 */
export const describeIncludeDisabledCommandsCapability = 'describe-include-disabled-commands.v1';

/**
 * Capability advertised by the CLI when `aspire ls --format json --stream` emits AppHost
 * candidates as newline-delimited JSON. Tooling uses this to avoid probing localized CLI errors
 * for CLIs that do not recognize the hidden streaming flag.
 * Keep in sync with `KnownCapabilities.LsJsonStream` in src/Aspire.Cli/Utils/ExtensionHelper.cs.
 */
export const lsJsonStreamCapability = 'ls-json-stream.v1';

/**
 * Capability advertised by the CLI when `aspire run` accepts the `--isolated` option.
 * Tooling uses this to avoid passing the option to older CLIs that reject it.
 * Keep in sync with `KnownCapabilities.IsolatedLaunch` in src/Aspire.Cli/Utils/ExtensionHelper.cs.
 */
export const isolatedLaunchCapability = 'isolated-launch.v1';

/**
 * Capability advertised by the CLI when `aspire agent mcp` is supported. Tooling uses this to
 * gate automatic pinned MCP server registration so an older CLI that does not advertise support
 * is never assumed to understand the command.
 * Keep in sync with `KnownCapabilities.AgentMcp` in src/Aspire.Cli/Utils/ExtensionHelper.cs.
 */
export const agentMcpCapability = 'agent-mcp.v1';

/**
 * First Aspire CLI version that accepts `aspire run --isolated`.
 */
export const isolatedLaunchMinimumVersion = '13.2.0';
