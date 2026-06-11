const express = require('express');
const cors = require('cors');
const path = require('path');

const app = express();
const PORT = process.env.PORT || 3000;

// Middleware
app.use(cors());
app.use(express.json());
app.use(express.urlencoded({ extended: true }));

// Root route for browsers (serves HTML)
app.get('/', (req, res) => {
    res.sendFile(path.join(__dirname, 'public', 'index.html'));
});

app.get('/api/welcome', (req, res) => {
    res.json({
            message: 'Welcome to AspireJavaScript Node.js App!',
            timestamp: new Date().toISOString(),
            version: '1.0.0'
        });
});

app.get('/api/health', (req, res) => {
    res.json({
        status: 'healthy',
        timestamp: new Date().toISOString()
    });
});

app.get('/api/weather', (req, res) => {
    // Mock weather data similar to other Aspire apps
    const weatherData = [
        {
            date: new Date().toISOString().split('T')[0],
            temperatureC: Math.floor(Math.random() * 35) - 5,
            summary: 'Sunny'
        },
        {
            date: new Date(Date.now() + 86400000).toISOString().split('T')[0],
            temperatureC: Math.floor(Math.random() * 35) - 5,
            summary: 'Cloudy'
        },
        {
            date: new Date(Date.now() + 172800000).toISOString().split('T')[0],
            temperatureC: Math.floor(Math.random() * 35) - 5,
            summary: 'Rainy'
        }
    ].map(item => ({
        ...item,
        temperatureF: Math.round((item.temperatureC * 9/5) + 32)
    }));

    res.json(weatherData);
});

// Serve remaining static files from public directory (after API routes)
app.use(express.static(path.join(__dirname, 'public')));

// Error handling middleware
app.use((err, req, res, next) => {
    console.error(err.stack);
    res.status(500).json({
        message: 'Something went wrong!',
        error: process.env.NODE_ENV === 'production' ? {} : err.stack
    });
});

// General 404 handler (for non-API routes)
app.use((req, res) => {
    res.status(404).json({
        message: 'Route not found',
        path: req.originalUrl
    });
});

// Start server
app.listen(PORT, () => {
    console.log(`Server is running on port ${PORT}`);
    console.log(`Environment: ${process.env.NODE_ENV || 'development'}`);
    console.log(`Visit: http://localhost:${PORT}`);
});

module.exports = app;