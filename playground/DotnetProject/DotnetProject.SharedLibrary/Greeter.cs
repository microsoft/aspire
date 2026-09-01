namespace DotnetProject.SharedLibrary;

/// <summary>
/// A trivial helper shared by both <c>DotnetProject.ApiService</c> and <c>DotnetProject.WorkerService</c>. 
/// Editing <see cref="Message"/> is the shared-library change that can be used to verify 
/// both services hot-reload together under <c>aspire run --watch</c>.
/// </summary>
public static class Greeter
{
    // Edit this to observe a shared-library change reflected by every service that references it.
    public const string Message = "Hello from the shared library!";

    public static string Greet(string caller) => $"{Message} (via {caller})";
}
