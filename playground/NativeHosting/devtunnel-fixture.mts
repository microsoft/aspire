import { createServer, request } from 'node:http';
import { readFile, writeFile, rm } from 'node:fs/promises';
import { join } from 'node:path';

// Local protocol fixture only. It imitates the narrow CLI operations/output while
// forwarding real HTTP to DCP-owned Nuxt. It never contacts the Dev Tunnels service.
const directory = process.env.ASPIRE_TUNNEL_FIXTURE_DIR;
if (!directory) throw new Error('This fixture requires an explicit private state directory.');
const [operation, id, ...args] = process.argv.slice(2);
const get = (flag: string) => args[args.indexOf(flag) + 1];
const path = (name: string) => {
    if (!/^native-[a-z0-9]+$/.test(name)) throw new Error('Fixture tunnel identity is invalid.');
    return join(directory, `${name}.json`);
};

if (operation === 'user' && id === 'show') {
    console.log(JSON.stringify({ authenticated: true, fixture: true }));
} else if (operation === 'create') {
    await writeFile(path(id), JSON.stringify({ tunnelId: id, ports: [] }), { flag: 'wx' });
    console.log(JSON.stringify({ tunnel: { tunnelId: id } }));
} else if (operation === 'port') {
    const action = id;
    const tunnel = args.shift()!;
    const state = JSON.parse(await readFile(path(tunnel), 'utf8'));
    const port = Number(get('--port-number'));
    if (!Number.isSafeInteger(port) || port < 1 || port > 65535) throw new Error('Fixture port is invalid.');
    if (action === 'create') state.ports = [port];
    else if (action === 'delete') state.ports = state.ports.filter((value: number) => value !== port);
    else throw new Error('Unsupported fixture port command.');
    await writeFile(path(tunnel), JSON.stringify(state));
    console.log(JSON.stringify({ port: { tunnelId: tunnel, portNumber: port, protocol: 'http' } }));
} else if (operation === 'delete') {
    await rm(path(id));
    console.log(JSON.stringify({ deleted: true }));
} else if (operation === 'host') {
    const state = JSON.parse(await readFile(path(id), 'utf8'));
    const port = state.ports[0];
    if (!Number.isSafeInteger(port)) throw new Error('Fixture tunnel has no port.');
    const server = createServer((incoming, response) => {
        const target = request({
            hostname: '127.0.0.1', port, method: incoming.method, path: incoming.url,
            headers: { ...incoming.headers, host: `127.0.0.1:${port}` }, timeout: 5000,
        }, upstream => {
            response.writeHead(upstream.statusCode ?? 502, upstream.headers);
            upstream.pipe(response);
        });
        target.on('timeout', () => target.destroy(new Error('Forwarded request timed out.')));
        target.on('error', () => { response.writeHead(502); response.end('Local fixture target unavailable.'); });
        incoming.pipe(target);
    });
    server.listen(0, '127.0.0.1', () => {
        const address = server.address();
        if (!address || typeof address === 'string') throw new Error('Fixture did not allocate an HTTP port.');
        const endpoint = `http://127.0.0.1:${address.port}/`;
        // Intentionally split recognized lines across multiple writes to exercise
        // incremental file/log parsing. The test also covers ANSI and inspect lines.
        process.stdout.write(`Hosting port: ${port}\nConnect via browser: `);
        setTimeout(() => {
            process.stdout.write(`${endpoint}\nReady to accept connections for tunnel: ${id}\n`);
        }, 350);
    });
    for (const signal of ['SIGTERM', 'SIGINT'] as const) {
        process.on(signal, () => {
            console.log('Connection to host tunnel relay closed.');
            server.close(() => process.exit(0));
            setTimeout(() => process.exit(0), 1000).unref();
        });
    }
} else {
    throw new Error('Unsupported local Dev Tunnels fixture command.');
}
