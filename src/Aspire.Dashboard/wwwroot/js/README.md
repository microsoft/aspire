# JavaScript Libraries

The Aspire Dashboard bundles a few JavaScript libraries.

## IMask

`imask-7.6.1.min.js` is the browser build from the `imask@7.6.1` npm package. It is loaded before Blazor because Fluent UI Blazor's `FluentNumberInput` checks for the global `IMask` object and otherwise attempts to load the library from a CDN. The dashboard bundles it locally so number inputs work offline and with the dashboard's `script-src 'self'` content security policy.

## Plotly

The default Plotly JS library is around 4MB in size (minified), as it supports many different chart types. Currently, we only use simple chart types, so can use the `basic` distribution which is around 1MB instead.

From [Plotly JS's docs](https://github.com/plotly/plotly.js/blob/22efc2fb76f4c890a2c33448e6f1485ecab77f26/dist/README.md#plotlyjs-basic):

> The `basic` partial bundle contains trace modules `bar`, `pie` and `scatter`.

If we ever want to show more chart types than those, we'll need to change the bundle we use.
