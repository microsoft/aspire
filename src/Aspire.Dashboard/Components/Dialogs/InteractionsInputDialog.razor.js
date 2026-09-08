const nativePasswordRevealStyleId = 'aspire-suppress-native-password-reveal';

export function suppressNativePasswordReveal(inputId) {
    const field = document.getElementById(inputId);
    if (!field) {
        throw new Error(`Password field '${inputId}' was not found.`);
    }

    const shadowRoot = field.shadowRoot;
    if (!shadowRoot) {
        throw new Error(`Password field '${inputId}' does not have a shadow root.`);
    }

    if (!shadowRoot.getElementById(nativePasswordRevealStyleId)) {
        // Edge supplies its own reveal control through ::-ms-reveal. FluentTextField keeps the
        // native input in a shadow root, so the override must be installed inside that root.
        // https://learn.microsoft.com/en-us/microsoft-edge/devtools-guide-chromium/css/css-pseudo-elements#-ms-reveal
        const style = document.createElement('style');
        style.id = nativePasswordRevealStyleId;
        style.textContent = 'input::-ms-reveal { display: none; }';
        shadowRoot.appendChild(style);
    }
}

export function togglePasswordVisibility(inputId) {
    const input = document.getElementById(inputId);
    if (input) {
        const currentType = input.getAttribute('type');
        const newType = currentType === 'password' ? 'text' : 'password';
        input.setAttribute('type', newType);
    }
}
