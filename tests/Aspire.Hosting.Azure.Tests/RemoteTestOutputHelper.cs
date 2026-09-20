// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.DotNet.RemoteExecutor;

namespace Aspire.Hosting.Azure.Tests;

// The remote process cannot use the parent's ITestOutputHelper directly. Write its logs to the
// child console so StartAndWait can redirect them to the parent test output helper.
internal sealed class RemoteTestOutputHelper : ITestOutputHelper
{
    public string Output => string.Empty;

    public void Write(string message) => Console.Write(message);

    public void Write(string format, params object[] args) => Console.Write(format, args);

    public void WriteLine(string message) => Console.WriteLine(message);

    public void WriteLine(string format, params object[] args) => Console.WriteLine(format, args);

    public static RemoteInvokeOptions CreateRemoteInvokeOptions()
    {
        var options = new RemoteInvokeOptions { Start = false };
        options.StartInfo.RedirectStandardError = true;
        options.StartInfo.RedirectStandardOutput = true;
        return options;
    }

    public static void StartAndWait(RemoteInvokeHandle handle, ITestOutputHelper testOutputHelper)
    {
        handle.Process.OutputDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null)
            {
                testOutputHelper.WriteLine($"[RemoteExecutor] {eventArgs.Data}");
            }
        };
        handle.Process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null)
            {
                testOutputHelper.WriteLine($"[RemoteExecutor] ERROR: {eventArgs.Data}");
            }
        };

        handle.Process.Start();
        handle.Process.BeginErrorReadLine();
        handle.Process.BeginOutputReadLine();

        if (!handle.Process.WaitForExit(handle.Options.TimeOut))
        {
            var message = $"Timed out after {handle.Options.TimeOut}ms waiting for remote process {handle.AssemblyName}!{handle.ClassName}.{handle.MethodName}.";
            testOutputHelper.WriteLine($"[RemoteExecutor] ERROR: {message}");

            // The non-zero exit code is expected because the timeout path terminates the child.
            // RemoteInvokeHandle.Dispose still checks for remote exceptions and releases its resources.
            handle.Options.CheckExitCode = false;
            try
            {
                handle.Process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // The process exited between WaitForExit returning false and Kill.
            }

            handle.Process.WaitForExit();
            throw new RemoteExecutionException(message);
        }

        // The timed overload waits for the process but not its asynchronous output handlers.
        handle.Process.WaitForExit();
    }
}
