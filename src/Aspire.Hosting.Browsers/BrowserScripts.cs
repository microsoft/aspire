// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;

namespace Aspire.Hosting;

/// <summary>
/// Loads the shared browser helper script and composes command-specific JavaScript for CDP evaluation.
/// </summary>
/// <remarks>
/// This type and <c>BrowserHelpers.js</c> split the browser automation script surface by runtime boundary:
/// <see cref="BrowserScripts"/> is the C# loader/composer that embeds arguments safely and sends one expression through
/// CDP, while <c>BrowserHelpers.js</c> is the browser-local runtime that can be reviewed and syntax-checked as normal
/// JavaScript.
///
/// The model is intentionally hybrid: <c>BrowserHelpers.js</c> is the fixed browser-side core, and the methods on this
/// type generate small per-command scripts that declare the command arguments, call into that core, and shape the JSON
/// response. CDP exposes low-level browser primitives, but commands such as snapshot, find, fill, wait, and state
/// management need to combine DOM APIs, accessibility-style metadata, browser events, and polling inside the page.
/// Keeping the reusable logic in a shared helper script gives agents a stable command contract while avoiding repeated
/// CDP round-trips for every DOM query or wait predicate. The helper script is embedded in this assembly, loaded once by
/// <see cref="LoadHelpers"/>, prepended to each generated command expression, and then evaluated in the active browser
/// target with CDP <c>Runtime.evaluate</c>.
/// </remarks>
internal static class BrowserScripts
{
    // These per-command script fragments run in the target page through CDP Runtime.evaluate. The fragments are generated
    // because each command has different arguments and a different response shape, but they all share the fixed helper
    // runtime above. Browser command arguments can come from the dashboard or resource-command API, so every string
    // argument embedded into script source must go through JsonLiteral. Numeric and Boolean arguments are validated and
    // typed before they reach this class.
    // The shared browser-side runtime is a .js embedded resource so reviewers can read and test the browser logic as
    // JavaScript instead of trying to reason about one giant interpolated C# string.
    private const string HelpersResourceName = "Aspire.Hosting.Browsers.Scripts.BrowserHelpers.js";
    private static readonly Lazy<string> s_helpers = new(LoadHelpers);

    private static string Helpers => s_helpers.Value;

    private static string LoadHelpers()
    {
        // Loading from an embedded resource keeps the browser-side runtime versioned with the hosting integration while
        // still letting the helper code live as a normal JavaScript file for review and syntax checks.
        using var stream = typeof(BrowserScripts).Assembly.GetManifestResourceStream(HelpersResourceName)
            ?? throw new InvalidOperationException($"Embedded browser helper script '{HelpersResourceName}' was not found.");
        using var reader = new StreamReader(stream);

        return reader.ReadToEnd();
    }

    /// <summary>
    /// Creates a script that summarizes the current page and its interactive elements.
    /// </summary>
    /// <remarks>
    /// This backs the <c>inspect-browser</c> resource command and the automatic snapshot captured after browser actions.
    /// It runs inside the page because the command needs DOM text, accessibility-style names, generated element refs,
    /// bounds, and current form values in one consistent snapshot.
    /// </remarks>
    public static string CreateSnapshotExpression(int maxElements, int maxTextLength)
    {
        return CreateExpression($$"""
{{Helpers}}
const maxElements = {{maxElements}};
const maxTextLength = {{maxTextLength}};
const elements = getInteractiveElements()
  .slice(0, maxElements)
  .map(describeElement);
return {
  action: 'snapshot',
  url: location.href,
  title: document.title,
  readyState: document.readyState,
  viewport: { width: innerWidth, height: innerHeight },
  text: normalizeText(document.body?.innerText ?? '').slice(0, maxTextLength),
  elements
};
""");
    }

    /// <summary>
    /// Creates a script that returns the active page URL, title, and readiness state.
    /// </summary>
    /// <remarks>
    /// This is used by the URL command so callers can confirm navigation and redirects without issuing a broader page
    /// snapshot.
    /// </remarks>
    public static string CreateUrlExpression()
    {
        return CreateExpression($$"""
{{Helpers}}
return {
  action: 'url',
  url: location.href,
  title: document.title,
  readyState: document.readyState
};
""");
    }

    /// <summary>
    /// Creates a script that lists frame and iframe elements visible to the active page.
    /// </summary>
    /// <remarks>
    /// This gives agents enough frame metadata to recognize iframe boundaries before they choose selectors or decide to
    /// use a lower-level CDP command.
    /// </remarks>
    public static string CreateFramesExpression()
    {
        return CreateExpression($$"""
{{Helpers}}
const frames = Array.from(document.querySelectorAll('iframe, frame')).map((element, index) => {
  let sameOrigin = false;
  let title = null;
  let href = null;
  try {
    sameOrigin = Boolean(element.contentWindow?.document);
    title = element.contentWindow?.document?.title ?? null;
    href = element.contentWindow?.location?.href ?? null;
  } catch {
    sameOrigin = false;
  }

  return {
    index: index + 1,
    selector: preferredSelector(element),
    tagName: element.tagName.toLowerCase(),
    name: element.getAttribute('name'),
    id: element.id || null,
    src: element.getAttribute('src'),
    title,
    url: href,
    sameOrigin,
    visible: isVisible(element)
  };
});

return {
  action: 'frames',
  url: location.href,
  count: frames.length,
  frames
};
""");
    }

    /// <summary>
    /// Creates a script that drives browser history navigation or reload from inside the page.
    /// </summary>
    /// <remarks>
    /// History APIs are page-local operations, so this keeps back, forward, and reload behavior in the same tracked
    /// session instead of starting a new browser navigation pipeline.
    /// </remarks>
    public static string CreateHistoryNavigationExpression(string action)
    {
        return CreateExpression($$"""
{{Helpers}}
const action = {{JsonLiteral(action)}};
const beforeUrl = location.href;
switch (action) {
  case 'back':
    history.back();
    break;
  case 'forward':
    history.forward();
    break;
  case 'reload':
    location.reload();
    break;
  default:
    throw new Error(`Unsupported navigation action '${action}'.`);
}

return {
  action,
  beforeUrl,
  url: location.href
};
""");
    }

    /// <summary>
    /// Creates a script that reads a focused page or element property.
    /// </summary>
    /// <remarks>
    /// This is the structured alternative to arbitrary evaluation for common assertions such as title, URL, text,
    /// attributes, element count, bounds, and computed styles.
    /// </remarks>
    public static string CreateGetExpression(string property, string? selector, string? name)
    {
        return CreateExpression($$"""
{{Helpers}}
const property = {{JsonLiteral(property)}};
const selector = {{JsonLiteral(selector)}};
const name = {{JsonLiteral(name)}};
const element = selector && property !== 'count' ? findElement(selector) : undefined;
let value;
switch (property) {
  case 'title':
    value = document.title;
    break;
  case 'url':
    value = location.href;
    break;
  case 'text':
    value = normalizeText(element ? (element.innerText || element.textContent) : (document.body?.innerText ?? ''));
    break;
  case 'html':
    value = element ? element.outerHTML : document.documentElement.outerHTML;
    break;
  case 'value':
    if (!element) {
      throw new Error("The 'value' property requires a selector.");
    }
    value = 'value' in element ? element.value : element.getAttribute('value');
    break;
  case 'attr':
    if (!element || !name) {
      throw new Error("The 'attr' property requires selector and name arguments.");
    }
    value = element.getAttribute(name);
    break;
  case 'count':
    if (!selector) {
      throw new Error("The 'count' property requires a selector.");
    }
    if (/^e([1-9]\d*)$/.test(selector)) {
      findElement(selector);
      value = 1;
    } else {
      value = document.querySelectorAll(selector).length;
    }
    break;
  case 'box':
    if (!element) {
      throw new Error("The 'box' property requires a selector.");
    }
    value = describeResolvedElement(element).bounds;
    break;
  case 'styles':
    if (!element) {
      throw new Error("The 'styles' property requires a selector.");
    }
    const styles = getComputedStyle(element);
    value = name
      ? styles.getPropertyValue(name)
      : {
          display: styles.display,
          visibility: styles.visibility,
          color: styles.color,
          backgroundColor: styles.backgroundColor,
          fontSize: styles.fontSize,
          fontWeight: styles.fontWeight
        };
    break;
  default:
    throw new Error(`Unsupported get property '${property}'.`);
}

return {
  action: 'get',
  url: location.href,
  property,
  selector,
  name,
  value,
  element: element ? describeResolvedElement(element) : undefined
};
""");
    }

    /// <summary>
    /// Creates a script that answers a common element-state question for one selector.
    /// </summary>
    /// <remarks>
    /// State checks such as visible, enabled, and checked require page-local DOM and style inspection; keeping them here
    /// lets command callers avoid raw JavaScript for routine readiness and assertion checks.
    /// </remarks>
    public static string CreateIsExpression(string state, string selector)
    {
        return CreateExpression($$"""
{{Helpers}}
const state = {{JsonLiteral(state)}};
const selector = {{JsonLiteral(selector)}};
const element = findElement(selector);
let value;
switch (state) {
  case 'visible':
    value = isVisible(element);
    break;
  case 'enabled':
    value = isEnabled(element);
    break;
  case 'checked':
    value = isChecked(element);
    break;
  default:
    throw new Error(`Unsupported element state '${state}'.`);
}

return {
  action: 'is',
  url: location.href,
  selector,
  state,
  value,
  element: describeResolvedElement(element)
};
""");
    }

    /// <summary>
    /// Creates a script that resolves user-facing locator criteria to one concrete element.
    /// </summary>
    /// <remarks>
    /// The resource command needs this browser-side because role, text, label, placeholder, and snapshot-ref lookup are
    /// based on live DOM and accessibility-style metadata rather than CDP target information.
    /// </remarks>
    public static string CreateFindExpression(string kind, string value, string? name, int index)
    {
        return CreateExpression($$"""
{{Helpers}}
const kind = {{JsonLiteral(kind)}};
const value = {{JsonLiteral(value)}};
const name = {{JsonLiteral(name)}};
const index = {{index}};
const element = findMatchingElement(kind, value, name, index);
if (!element) {
  throw new Error(`No element matched find criteria '${kind}' '${value}'.`);
}

return {
  action: 'find',
  url: location.href,
  kind,
  value,
  name,
  index,
  element: describeResolvedElement(element)
};
""");
    }

    /// <summary>
    /// Creates a script that scrolls to an element and applies a temporary visual highlight.
    /// </summary>
    /// <remarks>
    /// Highlighting is intentionally page-local CSS mutation so a human can verify the exact element an agent selected.
    /// </remarks>
    public static string CreateHighlightExpression(string selector)
    {
        return CreateExpression($$"""
{{Helpers}}
const selector = {{JsonLiteral(selector)}};
const element = findElement(selector);
element.scrollIntoView({ block: 'center', inline: 'center' });
if ('dataset' in element) {
  element.dataset.aspireBrowserHighlight = 'true';
}
element.style.outline = '3px solid #ffbf00';
element.style.outlineOffset = '2px';
element.style.boxShadow = '0 0 0 4px rgba(255, 191, 0, 0.35)';
return {
  action: 'highlight',
  url: location.href,
  selector,
  element: describeResolvedElement(element)
};
""");
    }

    /// <summary>
    /// Creates a script that evaluates caller-authored JavaScript and serializes the result.
    /// </summary>
    /// <remarks>
    /// This is the explicit escape hatch for diagnostics that are not modeled as first-class commands. The expression is
    /// still embedded as a string literal first so it cannot accidentally reshape the generated wrapper script.
    /// </remarks>
    public static string CreateEvaluateExpression(string expression)
    {
        return CreateExpression($$"""
{{Helpers}}
const expression = {{JsonLiteral(expression)}};
let evaluator;
try {
  evaluator = Function(`"use strict"; return (${expression});`);
} catch {
  evaluator = Function(`"use strict"; ${expression}`);
}

let value = evaluator();
if (typeof value === 'function') {
  value = value();
}

value = await Promise.resolve(value);
return {
  action: 'eval',
  url: location.href,
  expression,
  valueType: value === null ? 'null' : typeof value,
  value: serializeValue(value)
};
""");
    }

    /// <summary>
    /// Creates a script that reads, sets, or clears cookies visible to the active page.
    /// </summary>
    /// <remarks>
    /// Cookie state is tied to the page origin and document APIs, so this runs in the browser to seed or inspect
    /// authentication and personalization state for local development flows.
    /// </remarks>
    public static string CreateCookiesExpression(string action, string? name, string? value, string? domain, string? path)
    {
        return CreateExpression($$"""
{{Helpers}}
const action = {{JsonLiteral(action)}};
const name = {{JsonLiteral(name)}};
const value = {{JsonLiteral(value)}};
const domain = {{JsonLiteral(domain)}};
const path = {{JsonLiteral(path)}};
let cookies;
switch (action) {
  case 'get':
    cookies = getPageCookies();
    if (name) {
      cookies = cookies.filter(cookie => cookie.name === name);
    }
    break;
  case 'set':
    if (!name) {
      throw new Error("The 'set' cookie action requires a name.");
    }
    setPageCookie({ name, value: value ?? '', domain, path });
    cookies = getPageCookies().filter(cookie => cookie.name === name);
    break;
  case 'clear':
    if (name) {
      clearPageCookie(name, domain, path);
    } else {
      for (const cookie of getPageCookies()) {
        clearPageCookie(cookie.name, domain, path);
      }
    }
    cookies = getPageCookies();
    break;
  default:
    throw new Error(`Unsupported cookie action '${action}'.`);
}

return {
  action: 'cookies',
  url: location.href,
  cookieAction: action,
  name,
  cookies
};
""");
    }

    /// <summary>
    /// Creates a script that reads, sets, or clears localStorage or sessionStorage entries.
    /// </summary>
    /// <remarks>
    /// Storage commands run inside the page because Web Storage is scoped by origin and only exposed through browser DOM
    /// APIs.
    /// </remarks>
    public static string CreateStorageExpression(string area, string action, string? key, string? value)
    {
        return CreateExpression($$"""
{{Helpers}}
const area = {{JsonLiteral(area)}};
const action = {{JsonLiteral(action)}};
const key = {{JsonLiteral(key)}};
const value = {{JsonLiteral(value)}};
const storage = getStorage(area);
let result;
switch (action) {
  case 'get':
    result = key ? { [key]: storage.getItem(key) } : getStorageEntries(area);
    break;
  case 'set':
    if (!key) {
      throw new Error("The 'set' storage action requires a key.");
    }
    storage.setItem(key, value ?? '');
    result = { [key]: storage.getItem(key) };
    break;
  case 'clear':
    if (key) {
      storage.removeItem(key);
    } else {
      storage.clear();
    }
    result = key ? { [key]: storage.getItem(key) } : getStorageEntries(area);
    break;
  default:
    throw new Error(`Unsupported storage action '${action}'.`);
}

return {
  action: 'storage',
  url: location.href,
  area,
  storageAction: action,
  key,
  entries: result
};
""");
    }

    /// <summary>
    /// Creates a script that captures, restores, or clears combined browser state for the active origin.
    /// </summary>
    /// <remarks>
    /// This composes the cookie and Web Storage helpers so scenario setup can move between repeatable app states without
    /// requiring callers to issue several lower-level commands.
    /// </remarks>
    public static string CreateStateExpression(string action, string? state, bool clearExisting)
    {
        return CreateExpression($$"""
{{Helpers}}
const action = {{JsonLiteral(action)}};
const stateJson = {{JsonLiteral(state)}};
const clearExisting = {{JsonSerializer.Serialize(clearExisting)}};
const readState = () => ({
  cookies: getPageCookies(),
  localStorage: getStorageEntries('local'),
  sessionStorage: getStorageEntries('session')
});
switch (action) {
  case 'get':
    return {
      action: 'state',
      stateAction: action,
      url: location.href,
      origin: location.origin,
      state: readState()
    };
  case 'set': {
    if (!stateJson) {
      throw new Error("The 'set' state action requires a state JSON argument.");
    }

    // stateJson is first embedded as an escaped JavaScript string literal, then parsed inside the page so callers can
    // provide structured browser state without letting JSON text break out into script syntax. It is shaped like:
    // { "cookies": [{ "name": "...", "value": "..." }], "localStorage": { "key": "value" }, "sessionStorage": { "key": "value" } }.
    const parsedState = JSON.parse(stateJson);
    if (clearExisting) {
      for (const cookie of getPageCookies()) {
        clearPageCookie(cookie.name);
      }
    }

    for (const cookie of parsedState.cookies ?? []) {
      setPageCookie(cookie);
    }

    setStorageEntries('local', parsedState.localStorage, clearExisting);
    setStorageEntries('session', parsedState.sessionStorage, clearExisting);
    return {
      action: 'state',
      stateAction: action,
      url: location.href,
      origin: location.origin,
      clearExisting,
      state: readState()
    };
  }
  case 'clear':
    for (const cookie of getPageCookies()) {
      clearPageCookie(cookie.name);
    }
    localStorage.clear();
    sessionStorage.clear();
    return {
      action: 'state',
      stateAction: action,
      url: location.href,
      origin: location.origin,
      state: readState()
    };
  default:
    throw new Error(`Unsupported state action '${action}'.`);
}
""");
    }

    /// <summary>
    /// Creates a script that focuses and clicks the target element.
    /// </summary>
    /// <remarks>
    /// The script uses page DOM events so links, buttons, and custom controls run the same handlers they would for a
    /// local browser user.
    /// </remarks>
    public static string CreateClickExpression(string selector)
    {
        return CreateExpression($$"""
{{Helpers}}
const selector = {{JsonLiteral(selector)}};
const element = findElement(selector);
focusElement(element);
element.click();
return {
  action: 'click',
  url: location.href,
  selector,
  element: describeResolvedElement(element)
};
""");
    }

    /// <summary>
    /// Creates a script that dispatches a realistic double-click sequence at the target element.
    /// </summary>
    /// <remarks>
    /// Some controls distinguish single-click and double-click behavior, so this sends the mouse/click/dblclick event
    /// sequence from within the page rather than just calling one handler.
    /// </remarks>
    public static string CreateDoubleClickExpression(string selector)
    {
        return CreateExpression($$"""
{{Helpers}}
const selector = {{JsonLiteral(selector)}};
const element = findElement(selector);
focusElement(element);
const rect = element.getBoundingClientRect();
const clientX = rect.left + rect.width / 2;
const clientY = rect.top + rect.height / 2;
dispatchMouseEvent(element, 'mousedown', clientX, clientY);
dispatchMouseEvent(element, 'mouseup', clientX, clientY);
element.click();
dispatchMouseEvent(element, 'mousedown', clientX, clientY);
dispatchMouseEvent(element, 'mouseup', clientX, clientY);
element.click();
element.dispatchEvent(new MouseEvent('dblclick', { bubbles: true, cancelable: true, clientX, clientY, view: window }));
return {
  action: 'dblclick',
  url: location.href,
  selector,
  element: describeResolvedElement(element)
};
""");
    }

    /// <summary>
    /// Creates a script that sets an input-like element to a final value and dispatches input/change events.
    /// </summary>
    /// <remarks>
    /// This is the form-state command for flows where the final value matters more than simulating every key stroke.
    /// </remarks>
    public static string CreateFillExpression(string selector, string value)
    {
        return CreateExpression($$"""
{{Helpers}}
const selector = {{JsonLiteral(selector)}};
const value = {{JsonLiteral(value)}};
const element = findElement(selector);
focusElement(element);
setElementValue(element, value);
dispatchInputEvents(element);
return {
  action: 'fill',
  url: location.href,
  selector,
  value,
  element: describeResolvedElement(element)
};
""");
    }

    /// <summary>
    /// Creates a script that checks or unchecks a checkbox/radio-style element.
    /// </summary>
    /// <remarks>
    /// The script mutates the browser property and dispatches form events so application validation and binding logic
    /// observes the same state transition as a user action.
    /// </remarks>
    public static string CreateCheckExpression(string selector, bool isChecked)
    {
        return CreateExpression($$"""
{{Helpers}}
const selector = {{JsonLiteral(selector)}};
const checked = {{JsonSerializer.Serialize(isChecked)}};
const element = findElement(selector);
if (!('checked' in element)) {
  throw new Error(`Element '${selector}' is not checkable.`);
}

focusElement(element);
element.checked = checked;
dispatchInputEvents(element);
return {
  action: checked ? 'check' : 'uncheck',
  url: location.href,
  selector,
  checked: Boolean(element.checked),
  element: describeResolvedElement(element)
};
""");
    }

    /// <summary>
    /// Creates a script that moves focus to the target element and reports the active element selector.
    /// </summary>
    /// <remarks>
    /// Focus has to happen in the page so keyboard commands and focus-driven UI behavior use the browser's actual active
    /// element.
    /// </remarks>
    public static string CreateFocusExpression(string selector)
    {
        return CreateExpression($$"""
{{Helpers}}
const selector = {{JsonLiteral(selector)}};
const element = findElement(selector);
focusElement(element);
return {
  action: 'focus',
  url: location.href,
  selector,
  activeElementSelector: preferredSelector(document.activeElement),
  element: describeResolvedElement(element)
};
""");
    }

    /// <summary>
    /// Creates a script that dispatches a keydown or keyup event to a focused or selected target.
    /// </summary>
    /// <remarks>
    /// This supports held-key and modifier-key scenarios where key state is represented by separate down/up commands.
    /// </remarks>
    public static string CreateKeyEventExpression(string type, string? selector, string key)
    {
        return CreateExpression($$"""
{{Helpers}}
const type = {{JsonLiteral(type)}};
const selector = {{JsonLiteral(selector)}};
const key = {{JsonLiteral(key)}};
const element = getKeyboardTarget(selector);
focusElement(element);
dispatchKeyboardEvent(element, type, key);
return {
  action: type,
  url: location.href,
  selector: selector ?? preferredSelector(element),
  key,
  element: describeResolvedElement(element)
};
""");
    }

    /// <summary>
    /// Creates a script that types text into an element one character at a time.
    /// </summary>
    /// <remarks>
    /// Typing runs in the page so autocomplete, validation, and input listeners see keyboard and input events rather than
    /// a silent value assignment.
    /// </remarks>
    public static string CreateTypeExpression(string selector, string text)
    {
        return CreateExpression($$"""
{{Helpers}}
const selector = {{JsonLiteral(selector)}};
const text = {{JsonLiteral(text)}};
const element = findElement(selector);
focusElement(element);
for (const character of text) {
  const eventOptions = { key: character, bubbles: true, cancelable: true };
  element.dispatchEvent(new KeyboardEvent('keydown', eventOptions));
  element.dispatchEvent(new KeyboardEvent('keypress', eventOptions));
  appendText(element, character);
  element.dispatchEvent(new KeyboardEvent('keyup', eventOptions));
}
return {
  action: 'type',
  url: location.href,
  selector,
  text,
  element: describeResolvedElement(element)
};
""");
    }

    /// <summary>
    /// Creates a script that presses one key against a selected element or the current focus target.
    /// </summary>
    /// <remarks>
    /// This covers shortcuts, submit-on-enter, and focus navigation paths where one key press has semantic meaning beyond
    /// inserting text.
    /// </remarks>
    public static string CreatePressExpression(string? selector, string key)
    {
        return CreateExpression($$"""
{{Helpers}}
const selector = {{JsonLiteral(selector)}};
const key = {{JsonLiteral(key)}};
const element = selector ? findElement(selector) : document.activeElement;
if (!element || element === document.body) {
  throw new Error('No focused element is available. Provide a selector or focus an element first.');
}

focusElement(element);
const eventOptions = { key, bubbles: true, cancelable: true };
element.dispatchEvent(new KeyboardEvent('keydown', eventOptions));
if (key.length === 1 && ('value' in element || element.isContentEditable)) {
  const currentValue = element.isContentEditable ? element.textContent ?? '' : element.value ?? '';
  setElementValue(element, currentValue + key);
  dispatchInputEvents(element);
}

if (key === 'Enter' && element.form) {
  if (typeof element.form.requestSubmit === 'function') {
    element.form.requestSubmit();
  } else {
    element.form.submit();
  }
}

element.dispatchEvent(new KeyboardEvent('keyup', eventOptions));
return {
  action: 'press',
  url: location.href,
  selector: selector ?? preferredSelector(element),
  key,
  element: describeResolvedElement(element)
};
""");
    }

    /// <summary>
    /// Creates a script that scrolls to an element and dispatches pointer/mouse hover events.
    /// </summary>
    /// <remarks>
    /// Hover state is event-driven in many apps, so this runs in the page to trigger menus, tooltips, and CSS hover
    /// behavior before the next command.
    /// </remarks>
    public static string CreateHoverExpression(string selector)
    {
        return CreateExpression($$"""
{{Helpers}}
const selector = {{JsonLiteral(selector)}};
const element = findElement(selector);
element.scrollIntoView({ block: 'center', inline: 'center' });
const rect = element.getBoundingClientRect();
const clientX = rect.left + rect.width / 2;
const clientY = rect.top + rect.height / 2;
if (globalThis.PointerEvent) {
  const pointerEventInit = { bubbles: true, cancelable: true, clientX, clientY, pointerType: 'mouse', isPrimary: true };
  element.dispatchEvent(new PointerEvent('pointerover', pointerEventInit));
  element.dispatchEvent(new PointerEvent('pointerenter', pointerEventInit));
  element.dispatchEvent(new PointerEvent('pointermove', pointerEventInit));
}
dispatchMouseEvent(element, 'mouseover', clientX, clientY);
dispatchMouseEvent(element, 'mouseenter', clientX, clientY);
dispatchMouseEvent(element, 'mousemove', clientX, clientY);
return {
  action: 'hover',
  url: location.href,
  selector,
  element: describeResolvedElement(element)
};
""");
    }

    /// <summary>
    /// Creates a script that selects an option in a native select element.
    /// </summary>
    /// <remarks>
    /// The script resolves by option value or visible text and dispatches form events so bindings observe the selection.
    /// </remarks>
    public static string CreateSelectExpression(string selector, string value)
    {
        return CreateExpression($$"""
{{Helpers}}
const selector = {{JsonLiteral(selector)}};
const value = {{JsonLiteral(value)}};
const element = findElement(selector);
if (element.localName !== 'select') {
  throw new Error(`Element '${selector}' is not a select element.`);
}

const option = Array.from(element.options).find(option => option.value === value || normalizeText(option.textContent) === value);
if (!option) {
  throw new Error(`No option with value or text '${value}' was found for '${selector}'.`);
}

focusElement(element);
element.value = option.value;
option.selected = true;
dispatchInputEvents(element);
return {
  action: 'select',
  url: location.href,
  selector,
  value: option.value,
  label: normalizeText(option.textContent),
  element: describeResolvedElement(element)
};
""");
    }

    /// <summary>
    /// Creates a script that scrolls either the page window or a selected scroll container.
    /// </summary>
    /// <remarks>
    /// Scrolling is page-local so this can reveal lazy-loaded or viewport-dependent content and report the resulting
    /// scroll position.
    /// </remarks>
    public static string CreateScrollExpression(string? selector, int deltaX, int deltaY)
    {
        return CreateExpression($$"""
{{Helpers}}
const selector = {{JsonLiteral(selector)}};
const deltaX = {{deltaX}};
const deltaY = {{deltaY}};
const target = selector ? findElement(selector) : window;
if (target === window) {
  window.scrollBy({ left: deltaX, top: deltaY, behavior: 'auto' });
} else {
  target.scrollBy({ left: deltaX, top: deltaY, behavior: 'auto' });
}
return {
  action: 'scroll',
  url: location.href,
  selector,
  deltaX,
  deltaY,
  scroll: target === window
    ? { x: Math.round(window.scrollX), y: Math.round(window.scrollY) }
    : { x: Math.round(target.scrollLeft), y: Math.round(target.scrollTop) },
  element: target === window ? undefined : describeResolvedElement(target)
};
""");
    }

    /// <summary>
    /// Creates a script that brings an element into the viewport.
    /// </summary>
    /// <remarks>
    /// This prepares controls below the fold or inside scroll panes for subsequent mouse, keyboard, or snapshot commands.
    /// </remarks>
    public static string CreateScrollIntoViewExpression(string selector)
    {
        return CreateExpression($$"""
{{Helpers}}
const selector = {{JsonLiteral(selector)}};
const element = findElement(selector);
element.scrollIntoView({ block: 'center', inline: 'center' });
return {
  action: 'scroll-into-view',
  url: location.href,
  selector,
  scroll: { x: Math.round(window.scrollX), y: Math.round(window.scrollY) },
  element: describeResolvedElement(element)
};
""");
    }

    /// <summary>
    /// Creates a script that dispatches coordinate-based mouse actions.
    /// </summary>
    /// <remarks>
    /// Coordinate input is needed for canvas, drag-like, wheel, and hit-test-sensitive UI where selector-based commands
    /// are not precise enough.
    /// </remarks>
    public static string CreateMouseExpression(string action, int x, int y, string? button, int deltaX, int deltaY)
    {
        return CreateExpression($$"""
{{Helpers}}
const action = {{JsonLiteral(action)}};
const x = {{x}};
const y = {{y}};
const button = {{JsonLiteral(button)}};
const deltaX = {{deltaX}};
const deltaY = {{deltaY}};
let element;
switch (action) {
  case 'move':
    element = dispatchMouseAt('mousemove', x, y, button, deltaX, deltaY);
    break;
  case 'down':
    element = dispatchMouseAt('mousedown', x, y, button, deltaX, deltaY);
    break;
  case 'up':
    element = dispatchMouseAt('mouseup', x, y, button, deltaX, deltaY);
    break;
  case 'click':
    element = dispatchMouseAt('mousedown', x, y, button, deltaX, deltaY);
    dispatchMouseAt('mouseup', x, y, button, deltaX, deltaY);
    element.click?.();
    break;
  case 'wheel':
    element = dispatchMouseAt('wheel', x, y, button, deltaX, deltaY);
    break;
  default:
    throw new Error(`Unsupported mouse action '${action}'.`);
}

return {
  action: 'mouse',
  mouseAction: action,
  url: location.href,
  x,
  y,
  button: button ?? 'left',
  deltaX,
  deltaY,
  element: element instanceof Element ? describeResolvedElement(element) : undefined
};
""");
    }

    /// <summary>
    /// Creates a script that waits for a selector, page text, or both.
    /// </summary>
    /// <remarks>
    /// Polling runs inside the page to avoid repeated CDP round-trips and to evaluate visibility/text against a
    /// consistent DOM snapshot on each iteration.
    /// </remarks>
    public static string CreateWaitForExpression(string? selector, string? text, int timeoutMilliseconds)
    {
        return CreateExpression($$"""
{{Helpers}}
const selector = {{JsonLiteral(selector)}};
const text = {{JsonLiteral(text)}};
const timeoutMilliseconds = {{timeoutMilliseconds}};
if (!selector && !text) {
  throw new Error('Provide a selector, text, or both when waiting in the browser.');
}

const startedAt = Date.now();
while (Date.now() - startedAt < timeoutMilliseconds) {
  // Poll from inside the page instead of round-tripping over CDP for each check. Keep the interval coarse enough to
  // avoid creating a busy loop on pages under test.
  const element = selector ? document.querySelector(selector) : undefined;
  const selectorMatched = !selector || (element && isVisible(element));
  const textMatched = !text || normalizeText(document.body?.innerText ?? '').includes(text);
  if (selectorMatched && textMatched) {
    return {
      action: 'wait-for',
      url: location.href,
      selector,
      text,
      elapsedMilliseconds: Date.now() - startedAt,
      element: element ? describeResolvedElement(element) : undefined
    };
  }

  await new Promise(resolve => setTimeout(resolve, 100));
}

throw new Error(`Timed out after ${timeoutMilliseconds} ms waiting for ${selector ? `selector '${selector}'` : ''}${selector && text ? ' and ' : ''}${text ? `text '${text}'` : ''}.`);
""");
    }

    /// <summary>
    /// Creates a script that waits until the active page URL matches an expected value.
    /// </summary>
    /// <remarks>
    /// This supports redirect and client-side-routing flows where the next command should not run until location changes
    /// settle.
    /// </remarks>
    public static string CreateWaitForUrlExpression(string url, string match, int timeoutMilliseconds)
    {
        return CreateExpression($$"""
{{Helpers}}
const expectedUrl = {{JsonLiteral(url)}};
const match = {{JsonLiteral(match)}};
const timeoutMilliseconds = {{timeoutMilliseconds}};
// Regex matching is intentionally user-authored browser logic. The pattern is still a string literal here, not
// injected script, but a pathological regex can consume time until the command timeout expires.
const matchesUrl = value => {
  switch (match) {
    case 'exact':
      return value === expectedUrl;
    case 'regex':
      return new RegExp(expectedUrl).test(value);
    case 'contains':
      return value.includes(expectedUrl);
    default:
      throw new Error(`Unsupported URL match mode '${match}'.`);
  }
};
const startedAt = Date.now();
while (Date.now() - startedAt < timeoutMilliseconds) {
  if (matchesUrl(location.href)) {
    return {
      action: 'wait-for-url',
      url: location.href,
      expectedUrl,
      match,
      elapsedMilliseconds: Date.now() - startedAt
    };
  }

  await new Promise(resolve => setTimeout(resolve, 100));
}

throw new Error(`Timed out after ${timeoutMilliseconds} ms waiting for URL '${expectedUrl}' (${match}). Current URL: '${location.href}'.`);
""");
    }

    /// <summary>
    /// Creates a script that waits for document load readiness or an approximate network-idle state.
    /// </summary>
    /// <remarks>
    /// The browser does not provide the higher-level readiness shape through one CDP command here, so the script combines
    /// document.readyState with resource timing stability from inside the page.
    /// </remarks>
    public static string CreateWaitForLoadStateExpression(string state, int timeoutMilliseconds)
    {
        return CreateExpression($$"""
{{Helpers}}
const state = {{JsonLiteral(state)}};
const timeoutMilliseconds = {{timeoutMilliseconds}};
const startedAt = Date.now();
let stableSince = undefined;
let lastResourceCount = performance.getEntriesByType('resource').length;
const stateMatched = () => {
  switch (state) {
    case 'domcontentloaded':
      return document.readyState === 'interactive' || document.readyState === 'complete';
    case 'complete':
    case 'load':
      return document.readyState === 'complete';
    case 'networkidle': {
      // There is no browser event exposed here for "network idle", so approximate it by requiring a loaded document and
      // a stable resource timing count for a short window.
      if (document.readyState !== 'complete') {
        stableSince = undefined;
        lastResourceCount = performance.getEntriesByType('resource').length;
        return false;
      }

      const resourceCount = performance.getEntriesByType('resource').length;
      if (resourceCount !== lastResourceCount) {
        lastResourceCount = resourceCount;
        stableSince = undefined;
        return false;
      }

      stableSince ??= Date.now();
      return Date.now() - stableSince >= 500;
    }
    default:
      throw new Error(`Unsupported load state '${state}'.`);
  }
};
while (Date.now() - startedAt < timeoutMilliseconds) {
  if (stateMatched()) {
    return {
      action: 'wait-for-load-state',
      url: location.href,
      state,
      readyState: document.readyState,
      elapsedMilliseconds: Date.now() - startedAt
    };
  }

  await new Promise(resolve => setTimeout(resolve, 100));
}

throw new Error(`Timed out after ${timeoutMilliseconds} ms waiting for load state '${state}'. Current readyState: '${document.readyState}'.`);
""");
    }

    /// <summary>
    /// Creates a script that waits for one element to reach a requested state.
    /// </summary>
    /// <remarks>
    /// Element attachment, visibility, enabled state, and checked state are live DOM/style questions; polling them in the
    /// page prevents action commands from racing rendering or interactivity changes.
    /// </remarks>
    public static string CreateWaitForElementStateExpression(string selector, string state, int timeoutMilliseconds)
    {
        return CreateExpression($$"""
{{Helpers}}
const selector = {{JsonLiteral(selector)}};
const state = {{JsonLiteral(state)}};
const timeoutMilliseconds = {{timeoutMilliseconds}};
const elementMatchesState = element => {
  switch (state) {
    case 'attached':
      return Boolean(element);
    case 'detached':
      return !element;
    case 'visible':
      return Boolean(element) && isVisible(element);
    case 'hidden':
      return !element || !isVisible(element);
    case 'enabled':
      return Boolean(element) && isEnabled(element);
    case 'disabled':
      return Boolean(element) && !isEnabled(element);
    case 'checked':
      return Boolean(element) && isChecked(element);
    case 'unchecked':
      return Boolean(element) && !isChecked(element);
    default:
      throw new Error(`Unsupported element state '${state}'.`);
  }
};
const startedAt = Date.now();
while (Date.now() - startedAt < timeoutMilliseconds) {
  const element = document.querySelector(selector);
  if (elementMatchesState(element)) {
    return {
      action: 'wait-for-element-state',
      url: location.href,
      selector,
      state,
      elapsedMilliseconds: Date.now() - startedAt,
      element: element ? describeResolvedElement(element) : undefined
    };
  }

  await new Promise(resolve => setTimeout(resolve, 100));
}

throw new Error(`Timed out after ${timeoutMilliseconds} ms waiting for selector '${selector}' to become '${state}'.`);
""");
    }

    /// <summary>
    /// Creates a script that waits for a caller-authored page predicate to become truthy.
    /// </summary>
    /// <remarks>
    /// This is the explicit readiness escape hatch for app-specific conditions that the built-in selector, URL, and load
    /// state waits cannot express.
    /// </remarks>
    public static string CreateWaitForFunctionExpression(string function, int timeoutMilliseconds)
    {
        return CreateExpression($$"""
{{Helpers}}
const functionBody = {{JsonLiteral(function)}};
const timeoutMilliseconds = {{timeoutMilliseconds}};
const evaluatePredicate = async () => {
  // wait-for-function is intentionally an escape hatch for caller-authored page predicates. JsonLiteral prevents script
  // injection around the predicate text; Function() is the requested behavior for this command.
  let result = Function(`"use strict"; return (${functionBody});`)();
  if (typeof result === 'function') {
    result = result();
  }

  return Boolean(await Promise.resolve(result));
};
const startedAt = Date.now();
while (Date.now() - startedAt < timeoutMilliseconds) {
  if (await evaluatePredicate()) {
    return {
      action: 'wait-for-function',
      url: location.href,
      function: functionBody,
      elapsedMilliseconds: Date.now() - startedAt
    };
  }

  await new Promise(resolve => setTimeout(resolve, 100));
}

throw new Error(`Timed out after ${timeoutMilliseconds} ms waiting for function '${functionBody}'.`);
""");
    }

    /// <summary>
    /// Wraps a command body as the async JavaScript source sent to CDP.
    /// </summary>
    /// <remarks>
    /// Every generated command returns JSON text by value, avoiding remote object handles and keeping the C# command
    /// result parsing consistent across all browser-side scripts.
    /// </remarks>
    private static string CreateExpression(string body)
    {
        // Runtime.evaluate receives one JavaScript source string, not JSON parameters. Keep the wrapper fixed and only
        // compose bodies from validated scalar values plus JsonLiteral-escaped strings.
        return $$"""
(async () => {
{{body}}
})().then(result => JSON.stringify(result))
""";
    }

    // Runtime.evaluate accepts one JavaScript source string, so command arguments must be embedded as JavaScript syntax.
    // Serializing the value as JSON gives us exactly that syntax: null becomes the token null, "Save" becomes "Save",
    // and a selector like button[data-id="save"] keeps its quotes inside the literal instead of ending the surrounding
    // script. The default encoder also escapes control characters and HTML-sensitive characters, so text such as
    // </script> cannot change the generated script shape if this expression is ever logged or copied into an HTML
    // context for diagnostics.
    private static string JsonLiteral(string? value) => JsonSerializer.Serialize(value);
}
