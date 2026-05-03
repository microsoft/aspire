// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;

namespace Aspire.Hosting;

internal static class BrowserLogsBrowserAutomationScripts
{
    private const string Helpers = """
const normalizeText = value => String(value ?? '').replace(/\s+/g, ' ').trim();
const cssIdentifier = value => globalThis.CSS && CSS.escape ? CSS.escape(String(value)) : String(value).replace(/["\\#.:>+~[\]\s]/g, '\\$&');
const cssString = value => String(value).replace(/\\/g, '\\\\').replace(/"/g, '\\"');
const byAttribute = (name, value) => `[${name}="${cssString(value)}"]`;
const isVisible = element => {
  const style = globalThis.getComputedStyle(element);
  const rect = element.getBoundingClientRect();
  return style.visibility !== 'hidden' && style.display !== 'none' && rect.width > 0 && rect.height > 0;
};
const preferredSelector = element => {
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
const associatedLabel = element => {
  if (element.id) {
    const explicitLabel = document.querySelector(`label[for="${cssString(element.id)}"]`);
    if (explicitLabel) {
      return normalizeText(explicitLabel.innerText || explicitLabel.textContent);
    }
  }

  const label = element.closest('label');
  return label ? normalizeText(label.innerText || label.textContent) : undefined;
};
const implicitRole = element => {
  if (element.getAttribute('role')) {
    return element.getAttribute('role');
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
  || element.textContent
);
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
const getInteractiveElements = () => Array.from(document.querySelectorAll(interactiveSelector)).filter(isVisible);
const describeElement = (element, index) => {
  const rect = element.getBoundingClientRect();
  const tag = element.localName;
  const text = normalizeText(element.innerText || element.textContent);
  const value = 'value' in element ? element.value : undefined;
  const options = tag === 'select'
    ? Array.from(element.options).map(option => ({ value: option.value, label: normalizeText(option.textContent), selected: option.selected }))
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
const findElement = selector => {
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
  const normalizedValue = normalizeText(value);
  const normalizedName = normalizeText(name);
  switch (kind) {
    case 'role':
      return Array.from(document.querySelectorAll('*')).find(element => {
        if (implicitRole(element) !== normalizedValue) {
          return false;
        }

        return !normalizedName || accessibleName(element).includes(normalizedName);
      });
    case 'text':
      return Array.from(document.querySelectorAll('body *')).find(element => normalizeText(element.innerText || element.textContent).includes(normalizedValue));
    case 'label':
      return Array.from(document.querySelectorAll('input, textarea, select')).find(element => normalizeText(associatedLabel(element)).includes(normalizedValue));
    case 'placeholder':
      return Array.from(document.querySelectorAll('[placeholder]')).find(element => normalizeText(element.getAttribute('placeholder')).includes(normalizedValue));
    case 'alt':
      return Array.from(document.querySelectorAll('[alt]')).find(element => normalizeText(element.getAttribute('alt')).includes(normalizedValue));
    case 'title':
      return Array.from(document.querySelectorAll('[title]')).find(element => normalizeText(element.getAttribute('title')).includes(normalizedValue));
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

  try {
    return JSON.parse(JSON.stringify(value));
  } catch {
    return String(value);
  }
};
const setElementValue = (element, value) => {
  if (element.isContentEditable) {
    element.textContent = value;
    return;
  }

  if (!('value' in element)) {
    throw new Error(`Element '${preferredSelector(element)}' cannot receive text input.`);
  }

  const descriptor = Object.getOwnPropertyDescriptor(Object.getPrototypeOf(element), 'value');
  if (descriptor?.set) {
    descriptor.set.call(element, value);
  } else {
    element.value = value;
  }
};
const dispatchInputEvents = element => {
  element.dispatchEvent(new Event('input', { bubbles: true }));
  element.dispatchEvent(new Event('change', { bubbles: true }));
};
const dispatchMouseEvent = (element, type, clientX, clientY) => {
  const eventInit = { bubbles: true, cancelable: true, clientX, clientY, view: window };
  element.dispatchEvent(new MouseEvent(type, eventInit));
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
  const selectionStart = typeof element.selectionStart === 'number' ? element.selectionStart : currentValue.length;
  const selectionEnd = typeof element.selectionEnd === 'number' ? element.selectionEnd : selectionStart;
  setElementValue(element, `${currentValue.slice(0, selectionStart)}${text}${currentValue.slice(selectionEnd)}`);
  if (typeof element.setSelectionRange === 'function') {
    const position = selectionStart + text.length;
    element.setSelectionRange(position, position);
  }
  dispatchInputEvents(element);
};
""";

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

    public static string CreateHighlightExpression(string selector)
    {
        return CreateExpression($$"""
{{Helpers}}
const selector = {{JsonLiteral(selector)}};
const element = findElement(selector);
element.scrollIntoView({ block: 'center', inline: 'center' });
if ('dataset' in element) {
  element.dataset.aspireBrowserLogsHighlight = 'true';
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

    public static string CreateWaitForUrlExpression(string url, string match, int timeoutMilliseconds)
    {
        return CreateExpression($$"""
{{Helpers}}
const expectedUrl = {{JsonLiteral(url)}};
const match = {{JsonLiteral(match)}};
const timeoutMilliseconds = {{timeoutMilliseconds}};
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

    public static string CreateWaitForFunctionExpression(string function, int timeoutMilliseconds)
    {
        return CreateExpression($$"""
{{Helpers}}
const functionBody = {{JsonLiteral(function)}};
const timeoutMilliseconds = {{timeoutMilliseconds}};
const evaluatePredicate = async () => {
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

    private static string CreateExpression(string body)
    {
        return $$"""
(async () => {
{{body}}
})().then(result => JSON.stringify(result))
""";
    }

    private static string JsonLiteral(string? value) => JsonSerializer.Serialize(value);
}
