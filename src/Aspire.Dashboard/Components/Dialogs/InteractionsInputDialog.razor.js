export function togglePasswordVisibility(inputId) {
    const input = document.getElementById(inputId);
    if (input) {
        const currentType = input.getAttribute('type');
        const newType = currentType === 'password' ? 'text' : 'password';
        input.setAttribute('type', newType);
    }
}

export function focusElement(elementId) {
    const element = document.getElementById(elementId);
    if (element) {
        element.focus();
    }
}
