import { expect, test } from "@playwright/test";
import { resolveResourceByName } from "../src/lib/resourceNames";

/**
 * Pins `resolveResourceByName` to `ResourceViewModel.TryGetResourceByName`
 * (src/Aspire.Dashboard/Model/ResourceViewModel.cs), which is what makes a console or metrics deep
 * link resolve to the same resource in both UIs.
 */

const resource = (name: string, displayName: string) => ({ name, displayName });

test.describe("resolveResourceByName", () => {
  test("matches the instance name exactly", () => {
    const resources = [resource("empty-0000-dgwtqzkh", "empty-0000")];
    expect(resolveResourceByName(resources, "empty-0000-dgwtqzkh")?.name).toBe("empty-0000-dgwtqzkh");
  });

  test("falls back to a unique display name", () => {
    // This is the deep-link case: the URL carries the logical name, but resources are keyed by the
    // DCP instance name, whose suffix is regenerated on every AppHost run.
    const resources = [
      resource("empty-0000-dgwtqzkh", "empty-0000"),
      resource("empty-0001-abcdefgh", "empty-0001"),
    ];
    expect(resolveResourceByName(resources, "empty-0000")?.name).toBe("empty-0000-dgwtqzkh");
  });

  test("prefers an instance name over a display name owned by another resource", () => {
    const resources = [
      resource("worker", "worker-display"),
      resource("worker-xyz", "worker"),
    ];
    expect(resolveResourceByName(resources, "worker")?.name).toBe("worker");
  });

  test("refuses an ambiguous display name", () => {
    // Replicas share a display name. Picking one arbitrarily would quietly show the wrong replica's
    // logs, so the dashboard returns no match and the caller falls back to its default view.
    const resources = [
      resource("api-0", "api"),
      resource("api-1", "api"),
    ];
    expect(resolveResourceByName(resources, "api")).toBeNull();
  });

  test("compares case-insensitively", () => {
    const resources = [resource("Empty-0000-DGWTQZKH", "Empty-0000")];
    expect(resolveResourceByName(resources, "empty-0000")?.name).toBe("Empty-0000-DGWTQZKH");
    expect(resolveResourceByName(resources, "EMPTY-0000-dgwtqzkh")?.name).toBe("Empty-0000-DGWTQZKH");
  });

  test("returns null for missing and empty names", () => {
    const resources = [resource("api-0", "api")];
    expect(resolveResourceByName(resources, "nope")).toBeNull();
    expect(resolveResourceByName(resources, "")).toBeNull();
    expect(resolveResourceByName(resources, null)).toBeNull();
    expect(resolveResourceByName(resources, undefined)).toBeNull();
  });
});
