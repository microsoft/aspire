// FluentUI v5 uses CSS-based theming via data-theme attribute and CSS custom properties.
// The FAST-based design token system from v4 has been removed.

const currentThemeCookieName = "currentTheme";
const themeSettingDark = "Dark";
const themeSettingLight = "Light";

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
 * Applies the Light or Dark theme to the entire site
 * @param {string} theme The theme to use. Should be Light or Dark
 */
function applyTheme(theme) {
    setThemeOnDocument(theme);
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

initializeTheme();
