import express, { type Request, type Response } from 'express';
import { Pool } from 'pg';
import { createClient, type RedisClientType } from 'redis';

// Aspire assigns this service an HTTP endpoint and tells it which port to listen on via PORT.
const port = Number(process.env.PORT ?? 8080);

// The Aspire app host imported this service's azd `uses: [cache, orders]` edges and wired them with
// WithReference, which injects the dependencies' connection strings as ConnectionStrings__<name>.
//   - "cache"  -> the db.redis resource (Azure Managed Redis, azd's Azure Cache for Redis successor; runs as a Redis container locally)
//   - "orders" -> the db.postgres resource (Azure Database for PostgreSQL; runs as a Postgres container locally)
const cacheConnectionString = process.env['ConnectionStrings__cache'];
const ordersConnectionString = process.env['ConnectionStrings__orders'];

// Redis (StackExchange-style) connection string emitted by Aspire's Redis resource:
//   "host:port"                         (local container, no auth)
//   "host:port,password=<pwd>,ssl=true" (when a password / TLS is configured)
function parseRedisConnectionString(connectionString: string): { host: string; port: number; password?: string; tls: boolean } {
    const segments = connectionString.split(',');
    const [host, portText] = segments[0].split(':');

    let password: string | undefined;
    let tls = false;
    for (const segment of segments.slice(1)) {
        const separator = segment.indexOf('=');
        if (separator < 0) {
            continue;
        }
        const key = segment.slice(0, separator).trim().toLowerCase();
        const value = segment.slice(separator + 1).trim();
        if (key === 'password') {
            password = value;
        } else if (key === 'ssl' && value.toLowerCase() === 'true') {
            tls = true;
        }
    }

    return { host, port: Number(portText), password, tls };
}

// Postgres (Npgsql key-value) connection string emitted by Aspire's PostgreSQL resource:
//   "Host=host;Port=port;Username=postgres;Password=<pwd>"
function parsePostgresConnectionString(connectionString: string): {
    host: string;
    port: number;
    user?: string;
    password?: string;
    database: string;
} {
    const values: Record<string, string> = {};
    for (const segment of connectionString.split(';')) {
        if (segment.length === 0) {
            continue;
        }
        const separator = segment.indexOf('=');
        if (separator < 0) {
            continue;
        }
        values[segment.slice(0, separator).trim().toLowerCase()] = segment.slice(separator + 1).trim();
    }

    return {
        host: values['host'] ?? values['server'] ?? 'localhost',
        port: Number(values['port'] ?? '5432'),
        user: values['username'] ?? values['user id'] ?? values['user'],
        password: values['password'],
        // The server connection string has no database, so target the default "postgres" database.
        database: values['database'] ?? 'postgres',
    };
}

let redisClient: RedisClientType | undefined;
if (cacheConnectionString) {
    const redis = parseRedisConnectionString(cacheConnectionString);
    // Locally, Aspire runs Azure Cache for Redis as a Redis container that terminates TLS with the
    // .NET developer certificate (a self-signed cert for CN=localhost). Node rejects that cert by
    // default, so relax verification only when talking to a loopback host. Against real Azure Cache
    // for Redis (a non-loopback hostname with a publicly trusted cert) verification stays enabled.
    const isLoopbackHost = redis.host === 'localhost' || redis.host === '127.0.0.1' || redis.host === '::1';
    redisClient = createClient({
        socket: redis.tls
            ? { host: redis.host, port: redis.port, tls: true, rejectUnauthorized: !isLoopbackHost }
            : { host: redis.host, port: redis.port },
        password: redis.password,
    });
    redisClient.on('error', (error) => console.error('Redis error:', error));
    await redisClient.connect();
}

let postgresPool: Pool | undefined;
if (ordersConnectionString) {
    postgresPool = new Pool(parsePostgresConnectionString(ordersConnectionString));
}

const app = express();

app.get('/', (_req: Request, res: Response) => {
    res.type('html').send(`
        <!DOCTYPE html>
        <html>
        <head><title>Contoso Store</title></head>
        <body style="font-family: sans-serif">
          <h1>Contoso Store</h1>
          <p>This service was an existing <strong>azd</strong> service (a TypeScript app), adopted by
             Aspire via <code>builder.addAzdProject(...)</code> with no changes to the azd assets.</p>
          <ul>
            <li><a href="/cache">/cache</a> &mdash; increments a counter in the imported Redis cache</li>
            <li><a href="/catalog">/catalog</a> &mdash; queries the imported PostgreSQL database</li>
          </ul>
        </body>
        </html>`);
});

app.get('/cache', async (_req: Request, res: Response) => {
    if (!redisClient) {
        res.status(503).json({ error: 'Cache connection string (ConnectionStrings__cache) is not configured.' });
        return;
    }

    try {
        const pageHits = await redisClient.incr('page:hits');
        res.json({ backend: 'Azure Cache for Redis (running as a container)', pageHits });
    } catch (error) {
        res.status(500).json({ error: error instanceof Error ? error.message : 'Unknown cache error' });
    }
});

app.get('/catalog', async (_req: Request, res: Response) => {
    if (!postgresPool) {
        res.status(503).json({ error: 'Orders connection string (ConnectionStrings__orders) is not configured.' });
        return;
    }

    try {
        const result = await postgresPool.query<{ version: string }>('SELECT version()');
        res.json({ backend: 'Azure Database for PostgreSQL (running as a container)', serverVersion: result.rows[0].version });
    } catch (error) {
        res.status(500).json({ error: error instanceof Error ? error.message : 'Unknown database error' });
    }
});

app.listen(port, () => {
    console.log(`Contoso Store listening on port ${port}`);
    console.log(`  cache configured:   ${Boolean(cacheConnectionString)}`);
    console.log(`  orders configured:  ${Boolean(ordersConnectionString)}`);
});
