// Apply the stored or system theme before first paint. The full theme module is deferred, while
// an equivalent inline bootstrap would be blocked by the dashboard's script-src 'self' policy.
const themeCookie = document.cookie
    .split(";")
    .map(value => value.trim())
    .find(value => value.startsWith("currentTheme="))
    ?.slice("currentTheme=".length);

const initialTheme = themeCookie === "Light" || themeCookie === "Dark"
    ? themeCookie
    : window.matchMedia("(prefers-color-scheme: dark)").matches ? "Dark" : "Light";

document.documentElement.dataset.theme = initialTheme === "Dark" ? "dark" : "light";