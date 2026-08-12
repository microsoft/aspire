import aspire.*;

void main() throws Exception {
        var builder = DistributedApplication.CreateBuilder();
        builder.addPythonApp("python-script", ".", "main.py");
        builder.addPythonModule("python-module", ".", "uvicorn");
        builder.addPythonExecutable("python-executable", ".", "pytest");
        var uvicorn = builder.addUvicornApp("python-uvicorn", ".", "main:app");
        uvicorn.withVirtualEnvironment(".venv", false);
        uvicorn.withDebugging();
        uvicorn.withEntrypoint(EntrypointType.MODULE, "uvicorn");
        uvicorn.withPip(new WithPipOptions().install(true).installArgs(new String[] { "install", "-r", "requirements.txt" }));
        uvicorn.withUv(new WithUvOptions().install(false).args(new String[] { "sync" }));
        builder.build().run();
    }
