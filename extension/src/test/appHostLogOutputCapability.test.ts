import * as assert from 'assert';
import { getSupportedCapabilities } from '../capabilities';
import { addInteractionServiceEndpoints, IInteractionService } from '../server/interactionService';

suite('AppHost log output capability', () => {
    test('advertises the structured log endpoint it implements', () => {
        const methods: string[] = [];
        const connection = {
            onRequest: (method: string) => methods.push(method)
        };
        const interactionService = new Proxy({}, {
            get: () => () => undefined
        }) as IInteractionService;

        addInteractionServiceEndpoints(connection as any, interactionService, {} as any, callback => callback);

        assert.ok(getSupportedCapabilities().includes('apphost-log-output.v1'));
        assert.ok(getSupportedCapabilities().includes('message-actions.v1'));
        assert.ok(methods.includes('writeAppHostLogEntry'));
    });
});
