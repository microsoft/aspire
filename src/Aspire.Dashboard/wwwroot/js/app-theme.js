import {
    accentBaseColor,
    accentFillActive,
    accentFillFocus,
    accentFillHover,
    accentFillRest,
    accentForegroundActive,
    accentForegroundFocus,
    accentForegroundHover,
    accentForegroundRest,
    accentStrokeControlActive,
    accentStrokeControlFocus,
    accentStrokeControlHover,
    accentStrokeControlRest,
    baseLayerLuminance,
    SwatchRGB,
    fillColor,
    neutralLayerL2,
    neutralPalette,
    DesignToken,
    neutralFillLayerRestDelta,
    bodyFont,
    controlCornerRadius,
    layerCornerRadius,
    typeRampMinus2FontSize,
    typeRampMinus2LineHeight,
    typeRampMinus1FontSize,
    typeRampMinus1LineHeight,
    typeRampBaseFontSize,
    typeRampBaseLineHeight,
    typeRampPlus1FontSize,
    typeRampPlus1LineHeight,
    typeRampPlus2FontSize,
    typeRampPlus2LineHeight,
    typeRampPlus3FontSize,
    typeRampPlus3LineHeight,
    typeRampPlus4FontSize,
    typeRampPlus4LineHeight,
    typeRampPlus5FontSize,
    typeRampPlus5LineHeight,
    typeRampPlus6FontSize,
    typeRampPlus6LineHeight,
    baseHeightMultiplier,
    baseHorizontalSpacingMultiplier,
    designUnit,
    strokeWidth,
    focusStrokeWidth,
    focusStrokeOuter,
    focusStrokeInner,
    disabledOpacity,
    PaletteRGB
} from "/_content/Microsoft.FluentUI.AspNetCore.Components/Microsoft.FluentUI.AspNetCore.Components.lib.module.js";

const currentThemeCookieName = "currentTheme";
const themeSettingDark = "Dark";
const themeSettingLight = "Light";
const darkThemeLuminance = 0.17;
const lightThemeLuminance = 1.0;
const darknessLuminanceTarget = (-0.1 + Math.sqrt(0.21)) / 2;
const brandPurple = createSwatch(0x51, 0x2B, 0xD4);
const brandPrimary = createSwatch(0x74, 0x55, 0xDD);
const brandSecondary = createSwatch(0xB9, 0xAA, 0xEE);
const brandLight = createSwatch(0xDC, 0xD5, 0xF6);

function createSwatch(r, g, b) {
    return SwatchRGB.create(r / 255.0, g / 255.0, b / 255.0);
}

/**
 * Updates the current theme on the site based on the specified theme
 * @param {string} specifiedTheme
 */
export function updateTheme(specifiedTheme) {
    const effectiveTheme = getEffectiveTheme(specifiedTheme);

    applyTheme(effectiveTheme);
    setThemeCookie(specifiedTheme);

    return effectiveTheme;
}

/**
 * Returns the value of the currentTheme cookie.
 * @returns {string}
 */
export function getThemeCookieValue() {
    return getCookieValue(currentThemeCookieName);
}

export function getCurrentTheme() {
    return getEffectiveTheme(getThemeCookieValue());
}

/**
 * Returns the current system theme (Light or Dark)
 * @returns {string}
 */
function getSystemTheme() {
    let matched = window.matchMedia('(prefers-color-scheme: dark)').matches;

    if (matched) {
        return themeSettingDark;
    } else {
        return themeSettingLight;
    }
}

/**
 * Sets the currentTheme cookie to the specified value.
 * @param {string} theme
 */
function setThemeCookie(theme) {
    if (theme == themeSettingDark || theme == themeSettingLight) {
        // Cookie will expire after 1 year. Using a much larger value won't have an impact because
        // Chrome limits expiration to 400 days: https://developer.chrome.com/blog/cookie-max-age-expires
        // The cookie is reset when the dashboard loads to creating a sliding expiration.
        document.cookie = `${currentThemeCookieName}=${theme}; Path=/; expires=${new Date(new Date().getTime() + 1000 * 60 * 60 * 24 * 365).toGMTString()}`;
    } else {
        // Delete cookie for other values (e.g. System)
        document.cookie = `${currentThemeCookieName}=; Path=/; expires=Thu, 01 Jan 1970 00:00:00 UTC;`;
    }
}

/**
 * Sets the document data-theme attribute to the specified value.
 * @param {string} theme The theme to set. Should be Light or Dark.
 */
function setThemeOnDocument(theme) {

    if (theme === themeSettingDark) {
        document.documentElement.setAttribute('data-theme', 'dark');
    } else /* Light */ {
        document.documentElement.setAttribute('data-theme', 'light');
    }
}

/**
 *
 * @param {string} theme The theme to use. Should be Light or Dark.
 */
function setBaseLayerLuminance(theme) {
    const baseLayerLuminanceValue = getBaseLayerLuminanceForTheme(theme);
    baseLayerLuminance.withDefault(baseLayerLuminanceValue);
}

/**
 * Returns the value of the specified cookie, or the empty string if the cookie is not present
 * @param {string} cookieName
 * @returns {string}
 */
function getCookieValue(cookieName) {
    const cookiePieces = document.cookie.split(';');
    for (let index = 0; index < cookiePieces.length; index++) {
        if (cookiePieces[index].trim().startsWith(cookieName)) {
            const cookieKeyValue = cookiePieces[index].split('=');
            if (cookieKeyValue.length > 1) {
                return cookieKeyValue[1];
            }
        }
    }

    return "";
}

/**
 * Converts a setting value for the theme (Light, Dark, System or null/empty) into the effective theme that should be applied
 * @param {string} specifiedTheme The setting value to use to determine the effective theme. Anything other than Light or Dark will be treated as System
 * @returns {string} The actual theme to use based on the supplied setting. Will be either Light or Dark.
 */
function getEffectiveTheme(specifiedTheme) {
    if (specifiedTheme === themeSettingLight ||
        specifiedTheme === themeSettingDark) {
        return specifiedTheme;
    } else {
        return getSystemTheme();
    }
}

/**
 *
 * @param {string} theme The theme to use. Should be Light or Dark
 * @returns {string}
 */
function getBaseLayerLuminanceForTheme(theme) {
    if (theme === themeSettingDark) {
        return darkThemeLuminance;
    } else /* Light */ {
        return lightThemeLuminance;
    }
}

/**
 * Configures Fluent's accent seed and semantic color tokens from the approved Aspire brand palette.
 * Fluent UI Blazor exposes the underlying FAST design tokens from its JavaScript module. Setting those
 * tokens keeps JavaScript token consumers and emitted CSS custom properties aligned, unlike overriding
 * the generated custom properties in a linked stylesheet.
 * @param {string} theme The theme to use. Should be Light or Dark
 */
function setAccentColor(theme) {
    accentBaseColor.withDefault(brandPurple);

    // The adaptive recipes interpolate additional shades from accentBaseColor. Pin the semantic roles
    // used by Fluent controls so the rendered colors remain in the approved palette while retaining
    // distinct interaction states and WCAG contrast in each theme.
    const rest = theme === themeSettingDark ? brandSecondary : brandPurple;
    const hover = theme === themeSettingDark ? brandLight : brandPrimary;
    const active = rest;
    const focus = rest;
    // Fluent design tokens are scoped to an element subtree. The body already receives generated
    // defaults during Fluent initialization, so use the dashboard's dedicated ancestor to ensure
    // FAST emits these explicit semantic values and every Fluent Blazor component inherits them.
    const root = document.getElementById("aspire-design-system");
    if (!root) {
        throw new Error("The Aspire design-system token scope was not found.");
    }

    accentFillRest.setValueFor(root, rest);
    accentFillHover.setValueFor(root, hover);
    accentFillActive.setValueFor(root, active);
    accentFillFocus.setValueFor(root, focus);
    accentForegroundRest.setValueFor(root, rest);
    accentForegroundHover.setValueFor(root, hover);
    accentForegroundActive.setValueFor(root, active);
    accentForegroundFocus.setValueFor(root, focus);
    accentStrokeControlRest.setValueFor(root, rest);
    accentStrokeControlHover.setValueFor(root, hover);
    accentStrokeControlActive.setValueFor(root, active);
    accentStrokeControlFocus.setValueFor(root, focus);
}

/**
 * Configures the default background color to use for the body
 */
function setFillColor() {
    // Design specs say we should use --neutral-layer-2 as the fill color
    // for the body. Most of the web components use --fill-color as their
    // background color, so we need to make sure they get --neutral-layer-2
    // when they request --fill-color.
    fillColor.setValueFor(document.body, neutralLayerL2);
}

/**
 * Sets the base of the neutral ramp. Light mode remains neutral, while dark mode uses a restrained
 * blue-violet undertone so its surfaces do not read brown. Fluent regenerates every neutral layer,
 * stroke and fill from this midpoint, keeping the ramp cohesive.
 * @param {string} theme The theme to use. Should be Light or Dark
 */
function setNeutralBaseColor(theme) {
    const baseColor = theme === themeSettingDark
        ? { r: 0x7D / 255.0, g: 0x79 / 255.0, b: 0x86 / 255.0 }
        : { r: 0x7D / 255.0, g: 0x7D / 255.0, b: 0x7D / 255.0 };

    neutralPalette.withDefault(PaletteRGB.from(SwatchRGB.create(baseColor.r, baseColor.g, baseColor.b)));
}

/**
 * Applies the Light or Dark theme to the entire site
 * @param {string} theme The theme to use. Should be Light or Dark
 */
function applyTheme(theme) {
    setBaseLayerLuminance(theme);
    // Set the neutral ramp base before deriving the fill color, since the body fill is taken
    // from neutralLayerL2 (which is generated from the neutral palette we're adjusting here).
    setNeutralBaseColor(theme);
    setFillColor();
    // Accent recipes depend on the fill color. Apply the explicit brand semantics after all recipe
    // inputs are settled so a dependency update does not regenerate and re-emit interpolated colors.
    setAccentColor(theme);
    setThemeOnDocument(theme);
}

/**
 *
 * @param {Palette} palette
 * @param {number} baseLayerLuminance
 * @returns {number}
 */
function neutralLayer1Index(palette, baseLayerLuminance) {
    return palette.closestIndexOf(SwatchRGB.create(baseLayerLuminance, baseLayerLuminance, baseLayerLuminance));
}

/**
 *
 * @param {Palette} palette
 * @param {Swatch} reference
 * @param {number} baseLayerLuminance
 * @param {number} layerDelta
 * @param {number} hoverDeltaLight
 * @param {number} hoverDeltaDark
 * @returns {Swatch}
 */
function neutralLayerHoverAlgorithm(palette, reference, baseLayerLuminance, layerDelta, hoverDeltaLight, hoverDeltaDark) {
    const baseIndex = neutralLayer1Index(palette, baseLayerLuminance);
    // Determine both the size of the delta (from the value passed in) and the direction (if the current color is dark,
    // the hover color will be a lower index (lighter); if the current color is light, the hover color will be a higher index (darker))
    const hoverDelta = isDark(reference) ? hoverDeltaDark * -1 : hoverDeltaLight;
    return palette.get(baseIndex + (layerDelta * -1) + hoverDelta);
}

/**
 *
 * @param {Swatch} color
 * @returns {boolean}
 */
function isDark(color) {
    return color.relativeLuminance <= darknessLuminanceTarget;
}

/**
 * Creates additional design tokens that are used to define the hover colors for the neutral layers
 * used in the site theme (neutral-layer-1 and neutral-layer-2, specifically). Unlike other -hover
 * variants, these are not created by the design system by default so we need to create them ourselves.
 * This is a lightly tweaked variant of other hover recipes used in the design system.
 */
function createAdditionalDesignTokens() {
    const neutralLayer1HoverLightDelta = DesignToken.create({ name: 'neutral-layer-1-hover-light-delta', cssCustomPropertyName: null }).withDefault(3);
    const neutralLayer1HoverDarkDelta = DesignToken.create({ name: 'neutral-layer-1-hover-dark-delta', cssCustomPropertyName: null }).withDefault(2);
    const neutralLayer2HoverLightDelta = DesignToken.create({ name: 'neutral-layer-2-hover-light-delta', cssCustomPropertyName: null }).withDefault(2);
    const neutralLayer2HoverDarkDelta = DesignToken.create({ name: 'neutral-layer-2-hover-dark-delta', cssCustomPropertyName: null }).withDefault(2);

    const neutralLayer1HoverRecipe = DesignToken.create({ name: 'neutral-layer-1-hover-recipe', cssCustomPropertyName: null }).withDefault({
        evaluate: (element, reference) =>
            neutralLayerHoverAlgorithm(
                neutralPalette.getValueFor(element),
                reference || fillColor.getValueFor(element),
                baseLayerLuminance.getValueFor(element),
                0, // No layer delta since this is for neutral-layer-1
                neutralLayer1HoverLightDelta.getValueFor(element),
                neutralLayer1HoverDarkDelta.getValueFor(element)
            ),
    });

    const neutralLayer2HoverRecipe = DesignToken.create({ name: 'neutral-layer-2-hover-recipe', cssCustomPropertyName: null }).withDefault({
        evaluate: (element, reference) =>
            neutralLayerHoverAlgorithm(
                neutralPalette.getValueFor(element),
                reference || fillColor.getValueFor(element),
                baseLayerLuminance.getValueFor(element),
                // Use the same layer delta used by the base recipe to calculate layer 2
                neutralFillLayerRestDelta.getValueFor(element),
                neutralLayer2HoverLightDelta.getValueFor(element),
                neutralLayer2HoverDarkDelta.getValueFor(element)
            ),
    });

    // Creates the --neutral-layer-1-hover custom CSS property
    DesignToken.create('neutral-layer-1-hover').withDefault((element) =>
        neutralLayer1HoverRecipe.getValueFor(element).evaluate(element),
    );

    // Creates the --neutral-layer-2-hover custom CSS property
    DesignToken.create('neutral-layer-2-hover').withDefault((element) =>
        neutralLayer2HoverRecipe.getValueFor(element).evaluate(element),
    );
}

/**
 * Wires Fluent's design tokens to the --aspire-* CSS variables defined in
 * tokens.css. Fluent applies these tokens through a constructable stylesheet in
 * document.adoptedStyleSheets, which wins the cascade over <link>ed CSS, so they
 * can't be overridden from tokens.css directly. Pointing each Fluent token at a
 * var() reference keeps the real values in tokens.css while ensuring they win.
 */
function wireAspireDesignTokens() {
    bodyFont.withDefault("var(--aspire-font-sans)");
    controlCornerRadius.withDefault("var(--aspire-radius-control)");
    layerCornerRadius.withDefault("var(--aspire-radius-layer)");

    // Typography ramp: wire every Fluent type-ramp step to its --aspire-type-* var
    // (tokens.css). Fluent's ramp steps are independent tokens, so each one must be
    // wired individually for the single --aspire-type-scale knob to rescale the whole
    // ramp. Both font-size and line-height are wired so vertical rhythm scales too.
    typeRampMinus2FontSize.withDefault("var(--aspire-type-minus-2-size)");
    typeRampMinus2LineHeight.withDefault("var(--aspire-type-minus-2-line-height)");
    typeRampMinus1FontSize.withDefault("var(--aspire-type-minus-1-size)");
    typeRampMinus1LineHeight.withDefault("var(--aspire-type-minus-1-line-height)");
    typeRampBaseFontSize.withDefault("var(--aspire-type-base-size)");
    typeRampBaseLineHeight.withDefault("var(--aspire-type-base-line-height)");
    typeRampPlus1FontSize.withDefault("var(--aspire-type-plus-1-size)");
    typeRampPlus1LineHeight.withDefault("var(--aspire-type-plus-1-line-height)");
    typeRampPlus2FontSize.withDefault("var(--aspire-type-plus-2-size)");
    typeRampPlus2LineHeight.withDefault("var(--aspire-type-plus-2-line-height)");
    typeRampPlus3FontSize.withDefault("var(--aspire-type-plus-3-size)");
    typeRampPlus3LineHeight.withDefault("var(--aspire-type-plus-3-line-height)");
    typeRampPlus4FontSize.withDefault("var(--aspire-type-plus-4-size)");
    typeRampPlus4LineHeight.withDefault("var(--aspire-type-plus-4-line-height)");
    typeRampPlus5FontSize.withDefault("var(--aspire-type-plus-5-size)");
    typeRampPlus5LineHeight.withDefault("var(--aspire-type-plus-5-line-height)");
    typeRampPlus6FontSize.withDefault("var(--aspire-type-plus-6-size)");
    typeRampPlus6LineHeight.withDefault("var(--aspire-type-plus-6-line-height)");

    // Control geometry: wire the remaining sizing/stroke recipes to their --aspire-*
    // vars. Fluent consumes these purely as CSS custom properties (no JS height-number
    // recipe exists in Fluent Blazor 4.14), so a var() default is safe here.
    baseHeightMultiplier.withDefault("var(--aspire-height-multiplier)");
    baseHorizontalSpacingMultiplier.withDefault("var(--aspire-horizontal-spacing-multiplier)");
    designUnit.withDefault("var(--aspire-design-unit)");
    strokeWidth.withDefault("var(--aspire-stroke-width)");
    focusStrokeWidth.withDefault("var(--aspire-focus-stroke-width)");
    focusStrokeOuter.withDefault("var(--dash-focus-ring-color)");
    focusStrokeInner.withDefault("var(--dash-focus-ring-color)");
    disabledOpacity.withDefault("var(--aspire-disabled-opacity)");
}

function initializeTheme() {
    const themeCookieValue = getThemeCookieValue();
    const effectiveTheme = getEffectiveTheme(themeCookieValue);

    applyTheme(effectiveTheme);

    // If a theme cookie has been set then set it again on page load.
    // This updates the cookie expiration date and creates a sliding expiration.
    if (themeCookieValue) {
        setThemeCookie(themeCookieValue);
    }
}

wireAspireDesignTokens();
createAdditionalDesignTokens();
initializeTheme();
