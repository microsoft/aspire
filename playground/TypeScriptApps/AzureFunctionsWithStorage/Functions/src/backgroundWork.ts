export const backgroundWorkQueueName = "background-work";
export const backgroundWorkQueueConnection = "workQueue";

export interface BackgroundWorkItem {
    name: string;
    requestedAt: string;
}
