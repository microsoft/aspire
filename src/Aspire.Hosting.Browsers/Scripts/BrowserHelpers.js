// Shared browser-side helpers injected into the target page by BrowserScripts.
// These helpers exist because the automation commands need page-local DOM semantics:
// accessible labels, generated selectors, browser events, storage/cookie state, and
// polling loops all have to run where the page APIs are available. Command-specific
// C# templates append small scripts after these helpers and return JSON across CDP.

// ---------------------------------------------------------------------------
// Text and selector helpers
// ---------------------------------------------------------------------------

const normalizeText = value => String(value ?? '').replace(/\s+/g, ' ').trim();

const cssIdentifier = value => {
  const text = String(value);

  // CSS.escape is available in modern Chromium. The fallback escapes the
  // characters that would otherwise alter the generated selector shape.
  return globalThis.CSS?.escape
    ? CSS.escape(text)
    : text.replace(/["\\#.:>+~[\]\s]/g, '\\$&');
};

const cssString = value => String(value)
  .replace(/\\/g, '\\\\')
  .replace(/"/g, '\\"');

const byAttribute = (name, value) => `[${name}="${cssString(value)}"]`;

const getElementText = element => normalizeText(element.innerText || element.textContent);

const isSensitiveValueElement = element => {
  if (element.localName !== 'input') {
    return false;
  }

  const type = (element.getAttribute('type') || 'text').toLowerCase();
  if (type === 'password') {
    return true;
  }

  const autocomplete = (element.getAttribute('autocomplete') || '').toLowerCase();
  return autocomplete
    .split(/\s+/)
    .some(token => token.includes('password') || token === 'one-time-code');
};

const getElementValue = element => {
  if (!('value' in element)) {
    return undefined;
  }

  const value = element.value;
  return value && isSensitiveValueElement(element) ? '[redacted]' : value;
};

const isVisible = element => {
  const style = globalThis.getComputedStyle(element);
  const rect = element.getBoundingClientRect();

  return style.visibility !== 'hidden'
    && style.display !== 'none'
    && rect.width > 0
    && rect.height > 0;
};

const preferredSelector = element => {
  // Prefer stable, human-readable selectors. The fallback is capped so large
  // pages cannot produce unbounded selector strings in snapshots and errors.
  if (element.id) {
    return `#${cssIdentifier(element.id)}`;
  }

  for (const name of ['data-testid', 'data-test', 'data-qa', 'aria-label', 'name']) {
    const value = element.getAttribute(name);
    if (value) {
      return `${element.localName}${byAttribute(name, value)}`;
    }
  }

  const path = [];
  let current = element;

  while (current && current.nodeType === Node.ELEMENT_NODE && path.length < 6) {
    let selector = current.localName;

    if (current.id) {
      selector += `#${cssIdentifier(current.id)}`;
      path.unshift(selector);
      break;
    }

    let index = 1;
    let sibling = current;
    while ((sibling = sibling.previousElementSibling)) {
      if (sibling.localName === current.localName) {
        index++;
      }
    }

    selector += `:nth-of-type(${index})`;
    path.unshift(selector);
    current = current.parentElement;
  }

  return path.join(' > ');
};

// ---------------------------------------------------------------------------
// Accessible metadata and snapshot helpers
// ---------------------------------------------------------------------------

const associatedLabel = element => {
  if (element.id) {
    // The id comes from the page DOM, not from command input. Escape it before
    // using it inside a label[for] selector.
    const explicitLabel = document.querySelector(`label[for="${cssString(element.id)}"]`);
    if (explicitLabel) {
      return getElementText(explicitLabel);
    }
  }

  const label = element.closest('label');
  return label ? getElementText(label) : undefined;
};

const implicitRole = element => {
  const explicitRole = element.getAttribute('role');
  if (explicitRole) {
    return explicitRole;
  }

  switch (element.localName) {
    case 'a':
      return element.hasAttribute('href') ? 'link' : undefined;
    case 'button':
      return 'button';
    case 'input': {
      const type = (element.getAttribute('type') || 'text').toLowerCase();

      if (type === 'checkbox') {
        return 'checkbox';
      }

      if (type === 'radio') {
        return 'radio';
      }

      if (type === 'button' || type === 'submit' || type === 'reset') {
        return 'button';
      }

      return 'textbox';
    }
    case 'select':
      return 'combobox';
    case 'textarea':
      return 'textbox';
    default:
      return undefined;
  }
};

const accessibleName = element => normalizeText(
  element.getAttribute('aria-label')
  || associatedLabel(element)
  || element.getAttribute('title')
  || element.getAttribute('placeholder')
  || element.innerText
  || element.textContent);

const interactiveSelector = [
  'a[href]',
  'button',
  'input',
  'textarea',
  'select',
  'summary',
  '[role]',
  '[contenteditable="true"]',
  '[onclick]',
  '[tabindex]:not([tabindex="-1"])'
].join(',');

const getInteractiveElements = () => Array
  .from(document.querySelectorAll(interactiveSelector))
  .filter(isVisible);

const describeElement = (element, index) => {
  // Snapshot output intentionally includes only serializable metadata. It does
  // not return live DOM nodes across the CDP boundary; callers interact again
  // with either a selector or the generated eN snapshot reference.
  const rect = element.getBoundingClientRect();
  const tag = element.localName;
  const text = getElementText(element);
  const value = getElementValue(element);
  const options = tag === 'select'
    ? Array.from(element.options).map(option => ({
        value: option.value,
        label: normalizeText(option.textContent),
        selected: option.selected
      }))
    : undefined;

  return {
    index,
    ref: index >= 0 ? `e${index + 1}` : undefined,
    selector: preferredSelector(element),
    tag,
    role: implicitRole(element),
    type: element.getAttribute('type') || undefined,
    name: element.getAttribute('name') || undefined,
    label: associatedLabel(element),
    text: text ? text.slice(0, 160) : undefined,
    value: value ? String(value).slice(0, 160) : undefined,
    placeholder: element.getAttribute('placeholder') || undefined,
    href: element.href || undefined,
    checked: 'checked' in element ? Boolean(element.checked) : undefined,
    disabled: 'disabled' in element ? Boolean(element.disabled) : undefined,
    visible: isVisible(element),
    bounds: {
      x: Math.round(rect.x),
      y: Math.round(rect.y),
      width: Math.round(rect.width),
      height: Math.round(rect.height)
    },
    options
  };
};

const describeResolvedElement = element => {
  const index = getInteractiveElements().indexOf(element);
  return describeElement(element, index);
};

// ---------------------------------------------------------------------------
// Element lookup helpers
// ---------------------------------------------------------------------------

const findElement = selector => {
  // Snapshot refs are local to the current page state. Resolve them before CSS
  // selectors so a browser command can safely refer to "e1" without requiring
  // that text to also be a valid CSS selector.
  const refMatch = /^e([1-9]\d*)$/.exec(selector);
  if (refMatch) {
    const elements = getInteractiveElements();
    const index = Number(refMatch[1]) - 1;
    const element = elements[index];

    if (!element) {
      throw new Error(`No element matched snapshot ref '${selector}'. Refresh the browser snapshot and try again.`);
    }

    return element;
  }

  const element = document.querySelector(selector);
  if (!element) {
    throw new Error(`No element matched selector '${selector}'.`);
  }

  return element;
};

const findMatchingElement = (kind, value, name, index) => {
  // The find command accepts a small set of locator strategies. Strategies
  // that call querySelector use the caller-provided selector directly; test-id
  // matching escapes the value because it is interpolated into a selector.
  const normalizedValue = normalizeText(value);
  const normalizedName = normalizeText(name);

  switch (kind) {
    case 'role':
      return Array.from(document.querySelectorAll('*')).find(element =>
        implicitRole(element) === normalizedValue
          && (!normalizedName || accessibleName(element).includes(normalizedName)));
    case 'text':
      return Array
        .from(document.querySelectorAll('body *'))
        .find(element => getElementText(element).includes(normalizedValue));
    case 'label':
      return Array
        .from(document.querySelectorAll('input, textarea, select'))
        .find(element => normalizeText(associatedLabel(element)).includes(normalizedValue));
    case 'placeholder':
      return Array
        .from(document.querySelectorAll('[placeholder]'))
        .find(element => normalizeText(element.getAttribute('placeholder')).includes(normalizedValue));
    case 'alt':
      return Array
        .from(document.querySelectorAll('[alt]'))
        .find(element => normalizeText(element.getAttribute('alt')).includes(normalizedValue));
    case 'title':
      return Array
        .from(document.querySelectorAll('[title]'))
        .find(element => normalizeText(element.getAttribute('title')).includes(normalizedValue));
    case 'testid':
      return document.querySelector(`[data-testid="${cssString(value)}"], [data-test="${cssString(value)}"], [data-qa="${cssString(value)}"]`);
    case 'first':
      return document.querySelector(value);
    case 'last': {
      const elements = Array.from(document.querySelectorAll(value));
      return elements.at(-1);
    }
    case 'nth': {
      const elements = Array.from(document.querySelectorAll(value));
      return elements[index - 1];
    }
    default:
      throw new Error(`Unsupported find kind '${kind}'.`);
  }
};

const serializeValue = value => {
  if (value instanceof Element) {
    return describeResolvedElement(value);
  }

  // Evaluation results must survive JSON.stringify before being returned to
  // .NET. Fall back to string conversion for page objects with cycles or other
  // non-JSON values.
  try {
    return JSON.parse(JSON.stringify(value));
  } catch {
    return String(value);
  }
};

// ---------------------------------------------------------------------------
// Cookie and storage helpers
// ---------------------------------------------------------------------------

const decodeCookieComponent = value => {
  try {
    return decodeURIComponent(value);
  } catch {
    return value;
  }
};

const encodeCookieComponent = value => encodeURIComponent(String(value ?? ''));

const getPageCookies = () => {
  if (!document.cookie) {
    return [];
  }

  return document.cookie
    .split(/;\s*/)
    .filter(Boolean)
    .map(cookieText => {
      const separator = cookieText.indexOf('=');
      const name = separator >= 0 ? cookieText.slice(0, separator) : cookieText;
      const value = separator >= 0 ? cookieText.slice(separator + 1) : '';

      return {
        name: decodeCookieComponent(name),
        value: decodeCookieComponent(value)
      };
    });
};

const setPageCookie = cookie => {
  if (!cookie.name) {
    throw new Error('Cookie name is required.');
  }

  const parts = [`${encodeCookieComponent(cookie.name)}=${encodeCookieComponent(cookie.value ?? '')}`];
  parts.push(`path=${cookie.path || '/'}`);

  if (cookie.domain) {
    parts.push(`domain=${cookie.domain}`);
  }

  if (cookie.expires) {
    parts.push(`expires=${cookie.expires}`);
  }

  document.cookie = parts.join('; ');
};

const clearPageCookie = (name, domain, path) => {
  setPageCookie({
    name,
    value: '',
    domain,
    path,
    expires: 'Thu, 01 Jan 1970 00:00:00 GMT'
  });
};

const getStorage = area => {
  switch (area) {
    case 'local':
      return localStorage;
    case 'session':
      return sessionStorage;
    default:
      throw new Error(`Unsupported storage area '${area}'.`);
  }
};

const getStorageEntries = area => {
  const storage = getStorage(area);
  const entries = Array.from({ length: storage.length }, (_, index) => {
    const key = storage.key(index);
    return [key, key === null ? null : storage.getItem(key)];
  });

  // Storage keys can be null if an item disappears during iteration.
  return Object.fromEntries(entries.filter(([key]) => key !== null));
};

const setStorageEntries = (area, entries, clearExisting) => {
  const storage = getStorage(area);

  if (clearExisting) {
    storage.clear();
  }

  for (const [key, value] of Object.entries(entries ?? {})) {
    storage.setItem(key, String(value ?? ''));
  }
};

// ---------------------------------------------------------------------------
// Input, keyboard, and mouse helpers
// ---------------------------------------------------------------------------

const setElementValue = (element, value) => {
  if (element.isContentEditable) {
    element.textContent = value;
    return;
  }

  if (!('value' in element)) {
    throw new Error(`Element '${preferredSelector(element)}' cannot receive text input.`);
  }

  // Use the prototype setter when available so frameworks that patch input
  // value tracking see the same change a user would make. Bubbling events are
  // dispatched separately by the caller.
  const descriptor = Object.getOwnPropertyDescriptor(Object.getPrototypeOf(element), 'value');
  if (descriptor?.set) {
    descriptor.set.call(element, value);
  } else {
    element.value = value;
  }
};

const dispatchInputEvents = element => {
  // Most frameworks update their model on input/change rather than on value
  // assignment, so emit both events after mutating user-editable state.
  element.dispatchEvent(new Event('input', { bubbles: true }));
  element.dispatchEvent(new Event('change', { bubbles: true }));
};

const dispatchMouseEvent = (element, type, clientX, clientY) => {
  element.dispatchEvent(new MouseEvent(type, {
    bubbles: true,
    cancelable: true,
    clientX,
    clientY,
    view: window
  }));
};

const getKeyboardTarget = selector => {
  if (selector) {
    return findElement(selector);
  }

  const activeElement = document.activeElement;
  if (!activeElement || activeElement === document.body) {
    throw new Error('No focused element is available. Provide a selector or focus an element first.');
  }

  return activeElement;
};

const dispatchKeyboardEvent = (element, type, key) => {
  element.dispatchEvent(new KeyboardEvent(type, {
    key,
    bubbles: true,
    cancelable: true
  }));
};

const mouseButton = button => {
  switch (button ?? 'left') {
    case 'left':
      return 0;
    case 'middle':
      return 1;
    case 'right':
      return 2;
    default:
      throw new Error(`Unsupported mouse button '${button}'.`);
  }
};

const dispatchMouseAt = (type, x, y, button, deltaX, deltaY) => {
  const target = document.elementFromPoint(x, y) || document.body || document.documentElement;
  const buttonNumber = mouseButton(button);
  const eventInit = {
    bubbles: true,
    cancelable: true,
    clientX: x,
    clientY: y,
    button: buttonNumber,
    buttons: type === 'mousedown' ? 1 : 0,
    view: window
  };

  if (type === 'wheel') {
    target.dispatchEvent(new WheelEvent('wheel', { ...eventInit, deltaX, deltaY }));
    return target;
  }

  target.dispatchEvent(new MouseEvent(type, eventInit));
  return target;
};

const focusElement = element => {
  element.scrollIntoView({ block: 'center', inline: 'center' });

  if (typeof element.focus === 'function') {
    element.focus({ preventScroll: true });
  }
};

const isEnabled = element => !element.disabled && element.getAttribute('aria-disabled') !== 'true';

const isChecked = element => {
  if ('checked' in element) {
    return Boolean(element.checked);
  }

  return element.getAttribute('aria-checked') === 'true';
};

const appendText = (element, text) => {
  if (element.isContentEditable) {
    // execCommand is deprecated but still gives contenteditable controls the
    // closest browser-native insertText behavior available from injected
    // script, including selection handling.
    if (document.queryCommandSupported?.('insertText')) {
      document.execCommand('insertText', false, text);
    } else {
      element.textContent = `${element.textContent ?? ''}${text}`;
    }

    dispatchInputEvents(element);
    return;
  }

  if (!('value' in element)) {
    throw new Error(`Element '${preferredSelector(element)}' cannot receive text input.`);
  }

  const currentValue = String(element.value ?? '');
  const selectionStart = typeof element.selectionStart === 'number'
    ? element.selectionStart
    : currentValue.length;
  const selectionEnd = typeof element.selectionEnd === 'number'
    ? element.selectionEnd
    : selectionStart;

  setElementValue(element, `${currentValue.slice(0, selectionStart)}${text}${currentValue.slice(selectionEnd)}`);

  if (typeof element.setSelectionRange === 'function') {
    const position = selectionStart + text.length;
    element.setSelectionRange(position, position);
  }

  dispatchInputEvents(element);
};
