import * as assert from 'assert';
import * as fs from 'fs';
import * as path from 'path';

type TelemetryInventory = {
    events: Record<string, unknown>;
};

function readTelemetryInventory(): TelemetryInventory {
    const inventoryPath = path.resolve(__dirname, '../../telemetry.json');
    return JSON.parse(fs.readFileSync(inventoryPath, 'utf8')) as TelemetryInventory;
}

suite('extension/telemetry.json', () => {
    test('event entity names are lowercase to match VS Code telemetry ingestion', () => {
        const inventory = readTelemetryInventory();
        const mixedCaseEntityNames = Object.keys(inventory.events).filter(name => name !== name.toLowerCase());

        assert.deepStrictEqual(mixedCaseEntityNames, []);
    });
});
