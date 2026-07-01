// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting;

/// <summary>
/// Extension methods for <see cref="InteractionInput"/>.
/// </summary>
public static class InteractionInputExtensions
{
    /// <summary>
    /// Opens a read-only stream for the file associated with a <see cref="InputType.FileChooser"/> input.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For <see cref="InputType.FileChooser"/> inputs, <see cref="InteractionInput.Value"/> holds the file path
    /// on disk and <see cref="InteractionInput.FileName"/> holds the user-facing file name. This method opens
    /// the file at that path for reading.
    /// </para>
    /// <para>
    /// The caller is responsible for disposing the returned stream.
    /// </para>
    /// </remarks>
    /// <param name="input">The interaction input containing the file path in <see cref="InteractionInput.Value"/>.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>
    /// A <see cref="ValueTask{TResult}"/> that resolves to a <see cref="Stream"/> for reading the file,
    /// or <see langword="null"/> if <see cref="InteractionInput.Value"/> is null or empty.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the input's <see cref="InteractionInput.InputType"/> is not <see cref="InputType.FileChooser"/>.
    /// </exception>
    /// <exception cref="FileNotFoundException">
    /// Thrown when the file path in <see cref="InteractionInput.Value"/> does not exist on disk.
    /// </exception>
    public static ValueTask<Stream?> OpenFileStreamAsync(this InteractionInput input, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken; // Reserved for future use when streaming over gRPC.

        if (input.InputType != InputType.FileChooser)
        {
            throw new InvalidOperationException(
                $"OpenFileStreamAsync can only be called on inputs with InputType.FileChooser. The input '{input.Name}' has InputType.{input.InputType}.");
        }

        if (string.IsNullOrEmpty(input.Value))
        {
            return new ValueTask<Stream?>((Stream?)null);
        }

        if (!File.Exists(input.Value))
        {
            throw new FileNotFoundException(
                $"The file specified by interaction input '{input.Name}' was not found.",
                input.Value);
        }

        var stream = new FileStream(input.Value, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, useAsync: true);
        return new ValueTask<Stream?>(stream);
    }
}
