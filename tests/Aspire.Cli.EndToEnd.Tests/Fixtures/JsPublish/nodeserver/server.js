const http = require('http');
const port = process.env.PORT || 3000;
const server = http.createServer((req, res) => {
  res.writeHead(200, { 'Content-Type': 'application/json' });
  res.end(JSON.stringify({ status: 'ok', method: 'PublishAsNodeServer' }));
});
server.listen(port, '0.0.0.0', () => console.log(`Listening on port ${port}`));
