(function () {
    const cookieName = 'Aspire_Dashboard_Experience';
    const configuredDefault = document.querySelector('meta[name="aspire-dashboard-default-experience"]')?.content;
    const cookieValue = document.cookie
        .split('; ')
        .find(value => value.startsWith(`${cookieName}=`))
        ?.split('=')[1];
    const experience = configuredDefault === 'legacy' ? 'legacy' : cookieValue || 'modern';
    const path = window.location.pathname;
    const isLegacyPath = path === '/legacy' || path.startsWith('/legacy/');
    const excluded = path === '/login'
        || path.startsWith('/error')
        || path.startsWith('/api/')
        || path.startsWith('/_')
        || path.startsWith('/terminal-window');

    if (excluded) {
        return;
    }

    if (experience === 'legacy' && !isLegacyPath) {
        window.location.replace('/legacy');
    } else if (experience === 'modern' && isLegacyPath) {
        window.location.replace('/');
    }
})();
