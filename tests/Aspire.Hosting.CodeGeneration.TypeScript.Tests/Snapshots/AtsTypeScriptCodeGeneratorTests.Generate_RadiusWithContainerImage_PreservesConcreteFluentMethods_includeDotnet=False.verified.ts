interface ProjectResource {
    withContainerImage(image: string): ProjectResourcePromise;
}

interface ProjectResourcePromise {
    withContainerImage(image: string): ProjectResourcePromise;
}

    private async _withContainerImageInternal(image: string): Promise<ProjectResource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle, image };
        const result = await this._client.invokeCapability<ProjectResourceHandle>(
            'Aspire.Hosting.Radius/withContainerImage',
            rpcArgs
        );
        return new ProjectResourceImpl(result, this._client);
    }

interface CSharpAppResource {
    withContainerImage(image: string): CSharpAppResourcePromise;
}

interface CSharpAppResourcePromise {
    withContainerImage(image: string): CSharpAppResourcePromise;
}

    private async _withContainerImageInternal(image: string): Promise<CSharpAppResource> {
        const rpcArgs: Record<string, unknown> = { builder: this._handle, image };
        const result = await this._client.invokeCapability<CSharpAppResourceHandle>(
            'Aspire.Hosting.Radius/withContainerImage',
            rpcArgs
        );
        return new CSharpAppResourceImpl(result, this._client);
    }