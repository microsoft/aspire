import * as assert from 'assert';
import * as fs from 'fs';
import * as path from 'path';
import { runInNewContext } from 'vm';

function generateSchemas(packageValueType: string): Map<string, string> {
    const scriptDirectory = path.resolve(__dirname, '../../scripts');
    const output = new Map<string, string>();
    const features = { Name: 'features', Type: 'object', Description: '', Required: false };
    const packages = {
        Name: 'packages', Type: 'object', Description: '', Required: false,
        AdditionalPropertiesType: packageValueType,
        ...(packageValueType === 'object' ? {
            SubProperties: [
                { Name: 'Source', Type: 'object', Description: '', Required: false },
                { Name: 'Version', Type: 'string', Description: '', Required: false },
                { Name: 'Path', Type: 'string', Description: '', Required: false }
            ]
        } : {})
    };
    const configInfo = {
        AvailableFeatures: [{
            Name: 'experimentalHostingIntegrations',
            Description: 'Enable external hosting integrations',
            DefaultValue: false
        }],
        LocalSettingsSchema: { Properties: [features] },
        GlobalSettingsSchema: { Properties: [features] },
        ConfigFileSchema: { Properties: [features, packages] }
    };
    runInNewContext(fs.readFileSync(path.join(scriptDirectory, 'generate-schema.js'), 'utf8'), {
        __dirname: scriptDirectory,
        process: { platform: process.platform, exit: () => assert.fail('Schema generation must not exit early') },
        console,
        require: (name: string) => {
            switch (name) {
                case 'child_process':
                    return { execSync: () => JSON.stringify(configInfo) };
                case 'path':
                    return path;
                case 'fs':
                    return {
                        existsSync: () => true,
                        writeFileSync: (file: string, content: string) => output.set(path.basename(file), content)
                    };
                default:
                    throw new Error(`Unexpected schema generator dependency: ${name}`);
            }
        }
    });
    return output;
}

suite('Settings schema generation', () => {
    test('All settings schemas expose the hosting integration flag with its default off', () => {
        const schemas = generateSchemas('object');
        assert.strictEqual(schemas.size, 3);
        for (const content of schemas.values()) {
            const schema = JSON.parse(content);
            assert.strictEqual(schema.properties.features.properties.experimentalHostingIntegrations.default, false);
        }
    });

    test('Package entries preserve short-form strings and use the converter wire shape', () => {
        const schemas = generateSchemas('object');
        const schema = JSON.parse(schemas.get('aspire-config.schema.json')!);
        const variants = schema.properties.packages.additionalProperties.anyOf;
        assert.deepStrictEqual(variants[0], { type: ['string', 'null'] });
        assert.deepStrictEqual(variants[1].properties, {
            source: { type: 'string', enum: ['nuget', 'project', 'npm'] },
            version: { type: ['string', 'null'] },
            path: { type: 'string' }
        });
        assert.deepStrictEqual(variants[1].required, ['source']);
        assert.deepStrictEqual(variants[1].allOf, [{
            if: { properties: { source: { enum: ['project', 'npm'] } } },
            then: { required: ['path'] }
        }]);
    });

    test('Older CLI package metadata continues to generate string values', () => {
        const schemas = generateSchemas('string');
        const schema = JSON.parse(schemas.get('aspire-config.schema.json')!);
        assert.deepStrictEqual(schema.properties.packages.additionalProperties, { type: 'string' });
    });
});
