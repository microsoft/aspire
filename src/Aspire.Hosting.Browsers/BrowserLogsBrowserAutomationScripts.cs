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
    selector: preferredSelector(element),
    tag,
    role: element.getAttribute('role') || undefined,
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
const findElement = selector => {
  const element = document.querySelector(selector);
  if (!element) {
    throw new Error(`No element matched selector '${selector}'.`);
  }

  return element;
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
const focusElement = element => {
  element.scrollIntoView({ block: 'center', inline: 'center' });
  if (typeof element.focus === 'function') {
    element.focus({ preventScroll: true });
  }
};
""";

    public static string CreateSnapshotExpression(int maxElements, int maxTextLength)
    {
        return CreateExpression($$"""
{{Helpers}}
const maxElements = {{maxElements}};
const maxTextLength = {{maxTextLength}};
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
const elements = Array.from(document.querySelectorAll(interactiveSelector))
  .filter(isVisible)
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
  element: describeElement(element, 0)
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
  element: describeElement(element, 0)
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
  element: describeElement(element, 0)
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
  element: describeElement(element, 0)
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
      element: element ? describeElement(element, 0) : undefined
    };
  }

  await new Promise(resolve => setTimeout(resolve, 100));
}

throw new Error(`Timed out after ${timeoutMilliseconds} ms waiting for ${selector ? `selector '${selector}'` : ''}${selector && text ? ' and ' : ''}${text ? `text '${text}'` : ''}.`);
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
