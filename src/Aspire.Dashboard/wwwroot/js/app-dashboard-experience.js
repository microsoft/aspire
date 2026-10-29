(function () {
    const cookieName = 'Aspire_Dashboard_Experience';
    const legacyAvailable = document.querySelector('meta[name="aspire-dashboard-legacy-available"]')?.content === 'true';
    const configuredDefault = document.querySelector('meta[name="aspire-dashboard-default-experience"]')?.content;
    const cookieValue = document.cookie
        .split('; ')
        .find(value => value.startsWith(`${cookieName}=`))
        ?.split('=')[1];
    const experience = legacyAvailable && configuredDefault === 'legacy'
        ? 'legacy'
        : legacyAvailable && cookieValue === 'legacy'
            ? 'legacy'
            : 'modern';
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

    // Pages that both experiences have, with the same route and query string below the experience's root, keep
    // their URL so links to them work in either experience. Other pages go to the experience's root.
    // Keep in sync with DashboardExperienceRedirectMiddleware.
    const sharedPageSegments = ['consolelogs', 'structuredlogs', 'traces', 'metrics', 'terminals'];
    const isSharedPage = pagePath => sharedPageSegments.includes(pagePath.split('/')[1]?.toLowerCase());
    const query = window.location.search;

    if (experience === 'legacy' && !isLegacyPath) {
        window.location.replace(isSharedPage(path) ? `/legacy${path}${query}` : '/legacy');
    } else if (experience === 'modern' && isLegacyPath) {
        const modernPath = path.substring('/legacy'.length);
        window.location.replace(isSharedPage(modernPath) ? `${modernPath}${query}` : '/');
    }
})();
