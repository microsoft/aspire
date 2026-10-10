import { runIntegrationHost } from './generated/integration-host.mjs';
import { compatiblePorts, configureTunnel, configureStorage, nativePorts } from './ats-ports.mts';

const fixture = process.env.NATIVE_HOSTING_TUNNEL_FIXTURE;
const storage = process.env.NATIVE_HOSTING_PORT_DATA_DIRECTORY;
if (!storage) throw new Error('NATIVE_HOSTING_PORT_DATA_DIRECTORY is required.');
configureStorage(storage);
configureTunnel(fixture ? {
    executable: process.execPath,
    prefix: [new URL('./devtunnel-fixture.mts', import.meta.url).pathname],
    environment: { ASPIRE_TUNNEL_FIXTURE_DIR: fixture },
    localFixture: true,
} : {
    executable: process.env.NATIVE_HOSTING_DEVTUNNEL ?? 'devtunnel',
    prefix: [], environment: {}, localFixture: false,
});
await runIntegrationHost({ packageName: 'NativeHosting.Experiment', integrations: [nativePorts, compatiblePorts] });
