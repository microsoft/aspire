#!/usr/bin/env node

/**
 * Generates JSON schemas for Aspire settings files at build time.
 * These schemas are used by VS Code to provide IntelliSense, validation, and hover documentation.
 */

const { execSync } = require('child_process');
const fs = require('fs');
const path = require('path');

// Determine the Aspire CLI path based on the OS
const isWindows = process.platform === 'win32';
const cliPath = path.join(__dirname, '..', '..', 'artifacts', 'bin', 'Aspire.Cli', 'Debug', 'net10.0', isWindows ? 'aspire.exe' : 'aspire');

// Output paths for the schemas (relative to extension directory)
const localSchemaOutputPath = path.join(__dirname, '..', 'schemas', 'aspire-settings.schema.json');
const globalSchemaOutputPath = path.join(__dirname, '..', 'schemas', 'aspire-global-settings.schema.json');
const configSchemaOutputPath = path.join(__dirname, '..', 'schemas', 'aspire-config.schema.json');
const globalConfigSchemaOutputPath = path.join(__dirname, '..', 'schemas', 'aspire-global-config.schema.json');

console.log('Generating Aspire settings schemas...');

try {
    // Check if CLI exists
    if (!fs.existsSync(cliPath)) {
        console.warn(`WARNING: Aspire CLI not found at ${cliPath}`);
        console.warn('Skipping schema generation. Run ./build.sh first to build the CLI.');
        process.exit(0); // Exit successfully to not break the build
    }

    // Get config info from CLI
    const output = execSync(`"${cliPath}" config info --json`, { encoding: 'utf8' });
    const configInfo = JSON.parse(output);

    // Ensure output directory exists
    const schemaDir = path.dirname(localSchemaOutputPath);
    if (!fs.existsSync(schemaDir)) {
        fs.mkdirSync(schemaDir, { recursive: true });
    }

    // Generate local settings schema (includes all properties)
    const localSchema = generateJsonSchema(configInfo, configInfo.localSettingsSchema, {
        id: 'https://json.schemastore.org/aspire-settings.json',
        title: 'Aspire Local Settings',
        description: 'Aspire CLI local configuration file (.aspire/settings.json)'
    });
    fs.writeFileSync(localSchemaOutputPath, JSON.stringify(localSchema, null, 2), 'utf8');
    console.log(`✓ Local schema generated: ${localSchemaOutputPath}`);
    console.log(`  - ${configInfo.localSettingsSchema.properties.length} top-level properties`);

    // Generate global settings schema (excludes local-only properties like appHostPath)
    const globalSchema = generateJsonSchema(configInfo, configInfo.globalSettingsSchema, {
        id: 'https://json.schemastore.org/aspire-global-settings.json',
        title: 'Aspire Global Settings',
        description: 'Aspire CLI global configuration file (~/.aspire/settings.json)'
    });
    fs.writeFileSync(globalSchemaOutputPath, JSON.stringify(globalSchema, null, 2), 'utf8');
    console.log(`✓ Global schema generated: ${globalSchemaOutputPath}`);
    console.log(`  - ${configInfo.globalSettingsSchema.properties.length} top-level properties`);

    console.log(`  - ${configInfo.availableFeatures.length} feature flags`);

    // Generate aspire.config.json schema (new unified format)
    if (configInfo.configFileSchema) {
        const configSchema = generateJsonSchema(configInfo, configInfo.configFileSchema, {
            id: 'https://json.schemastore.org/aspire-config.json',
            title: 'Aspire Configuration',
            description: 'Aspire CLI unified configuration file (aspire.config.json). Replaces .aspire/settings.json and apphost.run.json.'
        });
        fs.writeFileSync(configSchemaOutputPath, JSON.stringify(configSchema, null, 2), 'utf8');
        console.log(`✓ Config file schema generated: ${configSchemaOutputPath}`);
        console.log(`  - ${configInfo.configFileSchema.properties.length} top-level properties`);
    } else {
        console.warn('WARNING: ConfigFileSchema not available in config info output. Skipping aspire-config.schema.json generation.');
    }

    // Generate global aspire.config.json schema (excludes local-only properties like appHost)
    if (configInfo.globalConfigFileSchema) {
        const globalConfigSchema = generateJsonSchema(configInfo, configInfo.globalConfigFileSchema, {
            id: 'https://json.schemastore.org/aspire-global-config.json',
            title: 'Aspire Global Configuration',
            description: 'Aspire CLI global configuration file (~/.aspire/aspire.config.json). Does not include local-only properties like appHost.'
        });
        fs.writeFileSync(globalConfigSchemaOutputPath, JSON.stringify(globalConfigSchema, null, 2), 'utf8');
        console.log(`✓ Global config file schema generated: ${globalConfigSchemaOutputPath}`);
        console.log(`  - ${configInfo.globalConfigFileSchema.properties.length} top-level properties`);
    } else {
        console.warn('WARNING: GlobalConfigFileSchema not available in config info output. Skipping aspire-global-config.schema.json generation.');
    }
} catch (error) {
    console.error('ERROR: Failed to generate schema:', error.message);
    console.warn('Skipping schema generation. This may happen if the CLI is not built yet.');
    process.exit(0); // Exit successfully to not break the build
}

function generateJsonSchema(configInfo, settingsSchema, options) {
    const properties = {};
    const required = [];

    // Add each top-level property
    for (const prop of settingsSchema.properties) {
        properties[prop.name] = createPropertySchema(prop, configInfo);

        if (prop.required) {
            required.push(prop.name);
        }
    }

    // Allow $schema property so users can reference the schema in their files
    properties['$schema'] = {
        type: 'string',
        description: 'JSON Schema reference'
    };

    return {
        $schema: 'http://json-schema.org/draft-07/schema#',
        $id: options.id,
        type: 'object',
        title: options.title,
        description: options.description,
        properties,
        ...(required.length > 0 ? { required } : {}),
        additionalProperties: false
    };
}

/**
 * Creates a JSON Schema property definition from a CLI PropertyInfo object.
 * Handles both legacy flat schemas and new config file schemas with nested types.
 * - 'features' properties are expanded using availableFeatures metadata.
 * - Properties with subProperties are recursed into (typed objects or dictionary values).
 * - Properties with additionalPropertiesType describe dictionary value types.
 * - Legacy 'packages' property name is handled as a fallback for older CLI output.
 */
function createPropertySchema(prop, configInfo) {
    const schema = {
        description: prop.description
    };

    const lowerType = prop.type.toLowerCase();

    if (lowerType === 'string') {
        schema.type = 'string';
    } else if (lowerType === 'boolean') {
        schema.anyOf = [
            { type: 'boolean' },
            { type: 'string', enum: ['true', 'false'] }
        ];
    } else if (lowerType === 'number' || lowerType === 'integer') {
        schema.type = lowerType;
    } else if (lowerType === 'array') {
        schema.type = 'array';
        schema.items = {};
    } else if (lowerType === 'object') {
        schema.type = 'object';

        // Expand known features as individual boolean properties
        if (prop.name === 'features') {
            schema.properties = {};
            schema.additionalProperties = false;

            for (const feature of configInfo.availableFeatures) {
                schema.properties[feature.name] = {
                    anyOf: [
                        { type: 'boolean' },
                        { type: 'string', enum: ['true', 'false'] }
                    ],
                    description: feature.description,
                    default: feature.defaultValue
                };
            }
        } else if (prop.subProperties && prop.subProperties.length > 0) {
            if (prop.additionalPropertiesType === 'object') {
                // Dictionary with complex values (e.g., profiles)
                const valueProperties = {};
                for (const subProp of prop.subProperties) {
                    valueProperties[subProp.name] = createPropertySchema(subProp, configInfo);
                }
                schema.additionalProperties = {
                    type: 'object',
                    properties: valueProperties,
                    additionalProperties: false
                };
            } else {
                // Known typed object (e.g., appHost, sdk)
                schema.properties = {};
                for (const subProp of prop.subProperties) {
                    schema.properties[subProp.name] = createPropertySchema(subProp, configInfo);
                }
                schema.additionalProperties = false;
            }
        } else if (prop.additionalPropertiesType) {
            // Simple dictionary (e.g., packages: string -> string)
            schema.additionalProperties = {
                type: prop.additionalPropertiesType
            };
        } else if (prop.name === 'packages') {
            // Legacy fallback for older CLI output without additionalPropertiesType
            schema.additionalProperties = {
                type: 'string',
                description: 'Package version'
            };
        } else {
            schema.additionalProperties = true;
        }
    } else {
        schema.type = 'string';
    }

    return schema;
}
