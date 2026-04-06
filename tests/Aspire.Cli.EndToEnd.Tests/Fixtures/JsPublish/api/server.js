const http = require('http');
const port = process.env.PORT || 3001;
http.createServer((req, res) => {
  res.writeHead(200, { 'Content-Type': 'application/json' });
  res.end(JSON.stringify([
    { date: '2026-04-01', temperatureC: 22, summary: 'Warm' },
    { date: '2026-04-02', temperatureC: 18, summary: 'Cool' },
    { date: '2026-04-03', temperatureC: 30, summary: 'Hot' }
  ]));
}).listen(port, '0.0.0.0', () => console.log(`API on port ${port}`));
