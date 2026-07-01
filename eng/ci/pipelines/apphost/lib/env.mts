export function readEnv(name: string, defaultValue: string): string {
    const value = process.env[name];
    return value === undefined || value.length === 0 ? defaultValue : value;
}

export function readBooleanEnv(name: string, defaultValue: boolean): boolean {
    const value = process.env[name];
    if (value === undefined || value.length === 0) {
        return defaultValue;
    }

    switch (value.toLowerCase()) {
        case 'true':
        case '1':
            return true;
        case 'false':
        case '0':
            return false;
        default:
            throw new Error(`${name} must be true or false. Actual value: ${value}`);
    }
}
