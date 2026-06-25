// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// The HTML checkbox "indeterminate" state is a DOM property, not an attribute, so it can
// only be set from script. Blazor sets `checked`/`disabled` declaratively; this bridges
// the remaining indeterminate property for the Deck Checkbox component.
export function setIndeterminate(element, value) {
    if (element) {
        element.indeterminate = value;
    }
}
