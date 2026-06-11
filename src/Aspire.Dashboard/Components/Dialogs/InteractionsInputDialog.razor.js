export function togglePasswordVisibility(inputId) {
    const input = document.getElementById(inputId);
    if (input) {
        const currentType = input.getAttribute('type');
        const newType = currentType === 'password' ? 'text' : 'password';
        input.setAttribute('type', newType);
    }
}
