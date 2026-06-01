import * as assert from 'assert';
import { TestRunSessionManager } from '../dcp/TestRunSessionManager';

suite('Test run session manager', () => {
    test('acquire returns DCP environment and release revokes the lease', () => {
        const manager = new TestRunSessionManager(
            {
                address: 'localhost:1234',
                certificate: 'test-cert'
            },
            () => ['project', 'node']);

        const lease = manager.acquire({ debug: true });
        const dcpId = `${lease.sessionId}-webfrontend`;

        assert.match(lease.sessionId, /^aspire-extension-test-run-[0-9a-f]+$/);
        assert.strictEqual(lease.env.DEBUG_SESSION_PORT, 'localhost:1234');
        assert.strictEqual(lease.env.DEBUG_SESSION_SERVER_CERTIFICATE, 'test-cert');
        assert.strictEqual(lease.env.DCP_INSTANCE_ID_PREFIX, `${lease.sessionId}-`);
        assert.strictEqual(lease.env.DEBUG_SESSION_RUN_MODE, 'Debug');
        assert.deepStrictEqual(JSON.parse(lease.env.DEBUG_SESSION_INFO), {
            protocols_supported: ["2024-03-03", "2024-04-23", "2025-10-01"],
            supported_launch_configurations: ['project', 'node']
        });

        assert.strictEqual(manager.tryAuthorizeDcpRequest(dcpId, lease.env.DEBUG_SESSION_TOKEN)?.id, lease.id);

        manager.release(lease.id);

        assert.strictEqual(manager.tryAuthorizeDcpRequest(dcpId, lease.env.DEBUG_SESSION_TOKEN), undefined);
    });

    test('wrong token does not authorize a matching DCP instance id', () => {
        const manager = new TestRunSessionManager(
            {
                address: 'localhost:1234',
                certificate: 'test-cert'
            },
            () => ['project']);

        const lease = manager.acquire({ debug: false });

        assert.strictEqual(manager.tryAuthorizeDcpRequest(`${lease.sessionId}-api`, 'wrong-token'), undefined);
    });

    test('expired lease does not authorize matching DCP request', () => {
        let now = 1_000;
        const manager = new TestRunSessionManager(
            {
                address: 'localhost:1234',
                certificate: 'test-cert'
            },
            () => ['project'],
            {
                leaseLifetimeMs: 5_000,
                now: () => now
            });

        const lease = manager.acquire({ debug: false });
        const dcpId = `${lease.sessionId}-api`;

        assert.strictEqual(manager.tryAuthorizeDcpRequest(dcpId, lease.env.DEBUG_SESSION_TOKEN)?.id, lease.id);

        now += 5_001;

        assert.strictEqual(manager.tryAuthorizeDcpRequest(dcpId, lease.env.DEBUG_SESSION_TOKEN), undefined);
    });

    test('lease remains valid until release by default', () => {
        let now = 1_000;
        const manager = new TestRunSessionManager(
            {
                address: 'localhost:1234',
                certificate: 'test-cert'
            },
            () => ['project'],
            {
                now: () => now
            });

        const lease = manager.acquire({ debug: false });
        const dcpId = `${lease.sessionId}-api`;

        now += 60 * 60 * 1000;

        assert.strictEqual(manager.tryAuthorizeDcpRequest(dcpId, lease.env.DEBUG_SESSION_TOKEN)?.id, lease.id);

        manager.release(lease.id);

        assert.strictEqual(manager.tryAuthorizeDcpRequest(dcpId, lease.env.DEBUG_SESSION_TOKEN), undefined);
    });
});
