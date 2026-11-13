// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Globalization;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class RecordingTestOutputHelper(ITestOutputHelper outputHelper) : ITestOutputHelper
{
    private readonly ConcurrentQueue<string> _messages = new();

    public string Output => string.Join(Environment.NewLine, _messages);

    public string[] Messages => _messages.ToArray();

    public event Action<string>? MessageWritten;

    public void Write(string message)
    {
        _messages.Enqueue(message);
        outputHelper.Write(message);
        MessageWritten?.Invoke(message);
    }

    public void Write(string format, params object[] args) => Write(string.Format(CultureInfo.CurrentCulture, format, args));

    public void WriteLine(string message)
    {
        _messages.Enqueue(message);
        outputHelper.WriteLine(message);
        MessageWritten?.Invoke(message);
    }

    public void WriteLine(string format, params object[] args) => WriteLine(string.Format(CultureInfo.CurrentCulture, format, args));
}
