# Flask App Example for Aspire

This is a simple Flask application that demonstrates integration with Aspire.

## Features

- Application factory pattern (`create_app()`)
- Multiple endpoints (/, /health, /api/data)
- OpenTelemetry instrumentation
- JSON responses

## Endpoints

- `GET /` - Hello world endpoint
- `GET /health` - Health check endpoint
- `GET /api/data` - Returns sample data

## Running with Aspire

This app is configured to run via the Python.AppHost project using `AddPythonModule`:

```csharp
var flaskApp = builder.AddPythonModule("flask-app", "../flask_app", "flask")
    .WithArgs("run", "--host=0.0.0.0", "--port=8000")
    .WithHttpEndpoint(targetPort: 8000)
    .WithUv();
```

## Local Development

1. Create virtual environment:
   ```bash
   uv venv
   ```

2. Install dependencies:
   ```bash
   uv sync
   ```

3. Run the app:
   ```bash
   flask --app app:create_app run --debug
   ```

Or run directly:
```bash
python app.py
```
