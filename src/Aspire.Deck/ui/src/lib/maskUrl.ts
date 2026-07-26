const MASK = "\u25cf".repeat(8);

/**
 * Masks query-string parameter values in a URL for display.
 *
 * Endpoint URLs routinely carry credentials in the query string - the dashboard's own browser
 * token login link (`/login?t=<token>`) is the canonical example, and user resources commonly
 * embed SAS tokens or API keys the same way. Rendering the raw URL as link text, or leaving it in
 * a `title` tooltip, exposes those values to anyone looking at the screen or a screenshot.
 *
 * Mirrors `DashboardUIHelpers.MaskQueryStringValues` in the Blazor dashboard: parameter names are
 * preserved so the shape of the URL stays recognisable, every value becomes eight bullets, and the
 * result is display-only. Callers must keep the unmasked URL in `href` so navigation still works.
 *
 * @example
 * maskQueryStringValues("http://localhost:5000/login?t=token123")
 * // => "http://localhost:5000/login?t=\u25cf\u25cf\u25cf\u25cf\u25cf\u25cf\u25cf\u25cf"
 */
export function maskQueryStringValues(url: string): string {
  const questionMarkIndex = url.indexOf("?");
  if (questionMarkIndex === -1) {
    return url;
  }

  const baseUrl = url.slice(0, questionMarkIndex);
  // Strip any fragment first: "?a=b#frag" must not turn the fragment into part of a value.
  const rest = url.slice(questionMarkIndex + 1);
  const hashIndex = rest.indexOf("#");
  const queryString = hashIndex === -1 ? rest : rest.slice(0, hashIndex);
  const fragment = hashIndex === -1 ? "" : rest.slice(hashIndex);

  if (queryString.length === 0) {
    return url;
  }

  const maskedParts = queryString.split("&").map((pair) => {
    if (pair.length === 0) {
      return pair;
    }
    const equalsIndex = pair.indexOf("=");
    // A bare flag such as "?verbose" has no value to hide.
    const name = equalsIndex === -1 ? pair : pair.slice(0, equalsIndex);
    return `${decodeUriComponentSafe(name)}=${MASK}`;
  });

  return `${baseUrl}?${maskedParts.join("&")}${fragment}`;
}

function decodeUriComponentSafe(value: string): string {
  try {
    // Match the Blazor helper, which decodes names through QueryHelpers.ParseQuery before display.
    return decodeURIComponent(value.replace(/\+/g, " "));
  } catch {
    // Malformed percent-encoding must not break rendering of the surrounding URL.
    return value;
  }
}
