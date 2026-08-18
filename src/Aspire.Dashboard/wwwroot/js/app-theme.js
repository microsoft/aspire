const currentThemeCookieName = "currentTheme";
const themeSettingDark = "Dark";
const themeSettingLight = "Light";

export function updateTheme(specifiedTheme) {
    const effectiveTheme = getEffectiveTheme(specifiedTheme);

    applyEffectiveTheme(effectiveTheme);
    setThemeCookie(specifiedTheme);

    return effectiveTheme;
}

export function getThemeCookieValue() {
    return getCookieValue(currentThemeCookieName);
}

export function getCurrentTheme() {
    return getEffectiveTheme(getThemeCookieValue());
}

function getSystemTheme() {
    return window.matchMedia("(prefers-color-scheme: dark)").matches
        ? themeSettingDark
        : themeSettingLight;
}

function getEffectiveTheme(specifiedTheme) {
    return specifiedTheme === themeSettingLight || specifiedTheme === themeSettingDark
        ? specifiedTheme
        : getSystemTheme();
}

function applyEffectiveTheme(theme) {
    const value = theme === themeSettingDark ? "dark" : "light";
    document.documentElement.dataset.theme = value;

    if (document.body) {
        document.body.dataset.theme = value;
    }
}

function setThemeCookie(theme) {
    if (theme === themeSettingDark || theme === themeSettingLight) {
        const expires = new Date(Date.now() + 1000 * 60 * 60 * 24 * 365).toUTCString();
        document.cookie = `${currentThemeCookieName}=${theme}; Path=/; expires=${expires}`;
    } else {
        document.cookie = `${currentThemeCookieName}=; Path=/; expires=Thu, 01 Jan 1970 00:00:00 UTC;`;
    }
}

function getCookieValue(cookieName) {
    const prefix = `${cookieName}=`;
    const cookie = document.cookie
        .split(";")
        .map(value => value.trim())
        .find(value => value.startsWith(prefix));

    return cookie?.slice(prefix.length) ?? "";
}

const themeCookieValue = getThemeCookieValue();
applyEffectiveTheme(getEffectiveTheme(themeCookieValue));

if (themeCookieValue) {
    setThemeCookie(themeCookieValue);
}

window.matchMedia("(prefers-color-scheme: dark)").addEventListener("change", () => {
    if (!getThemeCookieValue()) {
        applyEffectiveTheme(getSystemTheme());
    }
});
