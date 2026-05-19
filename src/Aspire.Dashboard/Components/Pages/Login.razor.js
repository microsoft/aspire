export async function validateToken(token) {
    try {
        var response = await fetch('/api/validatetoken', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ token: token })
        });
        return response.text();
    } catch (ex) {
        return `Error validating token: ${ex}`;
    }
}
