export function isRecord(value) {
    return value !== null && typeof value === "object" && !Array.isArray(value);
}
export function errorMessage(error) {
    return isRecord(error) && typeof error.message === "string" ? error.message : String(error);
}
//# sourceMappingURL=validation.js.map