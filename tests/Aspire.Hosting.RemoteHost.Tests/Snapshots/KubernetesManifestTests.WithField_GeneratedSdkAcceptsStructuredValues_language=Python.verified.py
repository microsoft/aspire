
class BaseKubernetesObject:
    """Type class for BaseKubernetesObject."""

    def __init__(self, handle: Handle, client: AspireClient) -> None:
        self._handle = handle
        self._client = client

    def __repr__(self) -> str:
        return f"BaseKubernetesObject(handle={self._handle.handle_id})"

    @_uncached_property
    def handle(self) -> Handle:
        """The underlying object reference handle."""
        return self._handle


class BaseKubernetesResource:
    """Type class for BaseKubernetesResource."""

    def __init__(self, handle: Handle, client: AspireClient) -> None:
        self._handle = handle
        self._client = client

    def __repr__(self) -> str:
        return f"BaseKubernetesResource(handle={self._handle.handle_id})"

    @_uncached_property
    def handle(self) -> Handle:
        """The underlying object reference handle."""
        return self._handle


class KubernetesManifestResource:
    """Type class for KubernetesManifestResource."""

    def __init__(self, handle: Handle, client: AspireClient) -> None:
        self._handle = handle
        self._client = client

    def __repr__(self) -> str:
        return f"KubernetesManifestResource(handle={self._handle.handle_id})"

    @_uncached_property
    def handle(self) -> Handle:
        """The underlying object reference handle."""
        return self._handle

    def with_field(self, path: str, value: typing.Any) -> KubernetesManifestResource:
        """Adds or updates a manifest field using a dot-separated path."""
        rpc_args: dict[str, typing.Any] = {'context': self._handle}
        rpc_args['path'] = path
        rpc_args['value'] = value
        result = self._client.invoke_capability(
            'Aspire.Hosting.Kubernetes/withField',
            rpc_args,
        )
        return typing.cast(KubernetesManifestResource, result)


# ============================================================================
# Connection Helper
# ============================================================================

def _get_client(*, debug: bool, heartbeat_interval: int | None) -> AspireClient:
    '''
    Creates and connects to the Aspire AppHost.
    Reads connection info from environment variables set by `aspire run`.
    '''
    socket_path = os.environ.get('REMOTE_APP_HOST_SOCKET_PATH')
    if not socket_path:
        raise ValueError(
            'REMOTE_APP_HOST_SOCKET_PATH environment variable not set. '
            'Run this application using `aspire run`.'
        )

    client = AspireClient(socket_path, debug=debug, heartbeat_interval=heartbeat_interval)
    client.connect()
    auth_token = os.environ.get('ASPIRE_REMOTE_APPHOST_TOKEN')
    if not auth_token:
        raise ValueError(
            'ASPIRE_REMOTE_APPHOST_TOKEN environment variable not set. '
            'Run this application using `aspire run`.'
        )
    client.authenticate(auth_token)
    return client