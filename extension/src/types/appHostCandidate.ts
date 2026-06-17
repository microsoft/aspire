/**
 * A discovered AppHost project candidate as reported by `aspire ls --format json`.
 *
 * Defined in a neutral types module (rather than alongside the discovery service) so the
 * lightweight public-API and status modules can reference it without importing the heavier
 * `utils/appHostDiscovery` service. The CLI owns the `status` vocabulary
 * (see `AppHostCandidateStatus` in ../utils/appHostStatus), so the field stays a `string` and
 * a newer CLI value cannot break deserialization.
 */
export interface AppHostCandidate {
    relativePath: string;
    path: string;
    language: string;
    status: string;
}
