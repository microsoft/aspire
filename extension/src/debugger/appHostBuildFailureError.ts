/**
 * Identifies a failure at the AppHost build boundary without classifying unrelated
 * debug-configuration errors from their free-form messages.
 */
export class AppHostBuildFailureError extends Error {
    constructor(
        message: string,
        readonly debugConsoleOutputAlreadyWritten: boolean) {
        super(message);
        this.name = 'AppHostBuildFailureError';
    }
}
