import express, { Request, Response } from 'express';

const app = express();
const port = process.env.PORT || 3000;

type Forecast = {
    date: string;
    temperatureC: number;
    temperatureF: number;
    summary: string;
};

app.get('/', (_req: Request, res: Response) => {
    res.json({
        message: 'Hello from pnpm workspace Node.js API!',
        timestamp: new Date().toISOString()
    });
});

app.get('/api/health', (_req: Request, res: Response) => {
    res.json({
        status: 'healthy',
        timestamp: new Date().toISOString()
    });
});

app.get('/api/weather', (_req: Request, res: Response) => {
    const summaries = ['Sunny', 'Cloudy', 'Rainy'];
    const forecasts: Forecast[] = summaries.map((summary, index) => {
        const temperatureC = Math.floor(Math.random() * 35) - 5;
        return {
            date: new Date(Date.now() + index * 86400000).toISOString().split('T')[0],
            temperatureC,
            temperatureF: Math.round((temperatureC * 9 / 5) + 32),
            summary
        };
    });

    res.json(forecasts);
});

app.listen(port, () => {
    console.log(`Node API listening on port ${port}`);
});
