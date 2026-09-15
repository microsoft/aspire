// The dependency-free Node v3 programming model keeps this fixture focused on Core Tools
// and inspector attachment, rather than package installation or TypeScript compilation.
module.exports = async function () {
    const message = 'Aspire Node Functions debugger E2E';
    return { body: message };
};
