import type { ResourceCommandJson } from '../views/AppHostDataRepository';

/**
 * Sorts resource commands by registration order, then name as a tiebreaker, since the CLI keys
 * commands alphabetically in JSON. Approximates the dashboard order (highlighted-command floating
 * isn't carried through the CLI). See src/Aspire.Cli/Backchannel/ResourceSnapshotMapper.cs.
 */
export function compareResourceCommands(
    [nameA, a]: [string, ResourceCommandJson],
    [nameB, b]: [string, ResourceCommandJson]): number {
    const orderA = a.registrationOrder ?? 0;
    const orderB = b.registrationOrder ?? 0;
    return orderA !== orderB ? orderA - orderB : nameA.localeCompare(nameB);
}
