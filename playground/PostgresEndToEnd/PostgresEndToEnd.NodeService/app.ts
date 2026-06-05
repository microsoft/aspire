import express from 'express';
import { Pool } from 'pg';
import { DefaultAzureCredential } from '@azure/identity';
import { v4 as uuidv4 } from 'uuid';

const app = express();

const AZURE_DB_FOR_POSTGRES_SCOPE = "https://ossrdbms-aad.database.windows.net/.default";

interface EntraConnInfo {
    user: string;
    password: string;
}

/**
 * Decodes a JWT token to extract its payload claims.
 */
function decodeJwt(token: string): any {
    const payload = token.split('.')[1];
    const padding = '='.repeat((4 - payload.length % 4) % 4);
    const decodedPayload = Buffer.from(payload + padding, 'base64url').toString('utf-8');
    return JSON.parse(decodedPayload);
}

/**
 * Obtains connection information from Entra authentication for Azure PostgreSQL.
 * Acquires a token and extracts the username from the token claims.
 */
async function getEntraConnInfo(credential: DefaultAzureCredential): Promise<EntraConnInfo> {
    // Fetch a new token and extract the username
    const tokenResponse = await credential.getToken(AZURE_DB_FOR_POSTGRES_SCOPE);
    if (!tokenResponse) {
        throw new Error("Failed to acquire token from credential");
    }
    
    const token = tokenResponse.token;
    const claims = decodeJwt(token);
    
    const username = claims.upn || claims.preferred_username || claims.unique_name;
    if (!username) {
        throw new Error("Could not extract username from token. Have you logged in?");
    }

    return {
        user: username,
        password: token
    };
}

async function getPool() {
    let user = process.env.DB1_USERNAME;
    let password = process.env.DB1_PASSWORD;

    if (!password) {
        const credential = new DefaultAzureCredential();
        const connInfo = await getEntraConnInfo(credential);
        if (!user)
            user = connInfo.user;
        password = connInfo.password;
    } 

    // NB: DB1_URI contains a password and hence can't be used in the Pool which will reject it
    return new Pool({
        host: process.env.DB1_HOST,
        port: parseInt(process.env.DB1_PORT || '5432'),
        database: process.env.DB1_DATABASE,
        user,
        password,
        ssl: process.env.DB1_AZURE === "true" ? { rejectUnauthorized: true } : false,
    });
}

app.get('/', async (_req, res) => {
    const pool = await getPool();
    const client = await pool.connect();
    try {
        await client.query("CREATE TABLE IF NOT EXISTS entries (id UUID PRIMARY KEY);");
        const entryId = uuidv4();
        await client.query("INSERT INTO entries (id) VALUES ($1);", [entryId]);
        const result = await client.query("SELECT id FROM entries;");
        res.json({
            totalEntries: result.rowCount,
            entries: result.rows.map(r => r.id),
        });
    } finally {
        client.release();
        await pool.end();
    }
});

const port = process.env.PORT || 3000;
app.listen(port, () => console.log(`Node app listening on port ${port}`));
