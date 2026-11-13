/**
     * Adds a .NET project resource
     * @param options Additional options.
     * @deprecated Use addDotnetProject(name, path) from the Aspire.Hosting.Dotnet package instead.
     */
    addProject(name: string, projectPath: string, options?: AddProjectOptions): ProjectResourcePromise;