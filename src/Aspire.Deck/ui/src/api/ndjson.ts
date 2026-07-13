export async function readNdjson<T>(
  stream: ReadableStream<Uint8Array>,
  onItem: (item: T) => void,
): Promise<void> {
  const reader = stream.getReader();
  const decoder = new TextDecoder();
  let buffer = "";

  const emit = (line: string): void => {
    const value = line.trim();
    if (value !== "") {
      onItem(JSON.parse(value) as T);
    }
  };

  try {
    // Streams contain one JSON object per line, for example:
    //   {"resourceName":"api","lines":[{"lineNumber":1,"text":"Ready","isStdErr":false}]}\n
    // Network chunks can split anywhere inside that object, so retain the incomplete
    // suffix and only parse records after their newline delimiter arrives.
    while (true) {
      const { done, value } = await reader.read();
      if (done) {
        buffer += decoder.decode();
        break;
      }

      buffer += decoder.decode(value, { stream: true });
      let newlineIndex = buffer.indexOf("\n");
      while (newlineIndex >= 0) {
        emit(buffer.slice(0, newlineIndex));
        buffer = buffer.slice(newlineIndex + 1);
        newlineIndex = buffer.indexOf("\n");
      }
    }

    emit(buffer);
  } finally {
    reader.releaseLock();
  }
}
