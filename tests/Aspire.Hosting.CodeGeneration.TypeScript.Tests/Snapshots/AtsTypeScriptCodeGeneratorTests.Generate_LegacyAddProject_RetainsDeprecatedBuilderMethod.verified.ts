/**
     * Adds a .NET project resource
     * @param options Additional options.
     * @deprecated Use addDotnetProject from the Aspire.Hosting.Dotnet package instead.
     */
    addProject(name: string, projectPath: string, options?: AddProjectOptions): ProjectResourcePromise;