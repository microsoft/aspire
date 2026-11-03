import { createServer } from 'node:http';

const server = createServer((request, response) => {
    if (request.url === '/health') {
        response.writeHead(200, { 'Content-Type': 'application/json' });
        response.end(JSON.stringify({ status: 'ok' }));
        return;
    }

    if (request.url === '/message') {
        response.writeHead(200, { 'Content-Type': 'application/json' });
        response.end(JSON.stringify({ message: 'Hello from the Aspire backend' }));
        return;
    }

    response.writeHead(404);
    response.end();
});

server.listen(Number(process.env.PORT), process.env.HOST ?? '0.0.0.0');
for (const signal of ['SIGINT', 'SIGTERM']) {
    process.on(signal, () => server.close());
}
