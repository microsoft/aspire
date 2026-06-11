import express, { Request, Response } from 'express';
import { Pool } from 'pg';

const app = express();
const port = process.env.PORT || 3000;

// PostgreSQL connection from Aspire-injected URI (postgresql://user:pass@host:port/db)
const connectionString = process.env['DB_URI'];

let pool: Pool | null = null;
if (connectionString) {
    pool = new Pool({ connectionString });
}

app.use(express.json());

app.get('/', async (req: Request, res: Response) => {
    try {
        if (!pool) {
            res.json({
                message: 'Hello from Express + Aspire!',
                database: 'Not configured'
            });
            return;
        }

        const result = await pool.query('SELECT NOW() as now, current_database() as db');
        res.json({
            message: 'Hello from Express + Aspire!',
            database: result.rows[0].db,
            serverTime: result.rows[0].now
        });
    } catch (err) {
        console.error('Database error:', err);
        res.status(500).json({
            error: 'Database connection failed',
            details: err instanceof Error ? err.message : 'Unknown error'
        });
    }
});

app.get('/health', (req: Request, res: Response) => {
    res.json({ status: 'healthy' });
});

app.listen(port, () => {
    console.log(`Express server running on port ${port}`);
    console.log('Environment variables:');
    Object.keys(process.env).filter(k =>
        k.startsWith('ConnectionStrings') ||
        k.startsWith('services') ||
        k.toLowerCase().includes('db') ||
        k.toLowerCase().includes('postgres')
    ).forEach(k => {
        console.log(`  ${k}=${process.env[k]}`);
    });
    if (connectionString) {
        console.log(`Database connection configured: ${connectionString}`);
    } else {
        console.log('No database connection configured');
    }
});
