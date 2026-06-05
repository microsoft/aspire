import hljs from '/js/highlight-11.11.1.min.js'

export function highlightCodeBlocks(container) {
    if (!container) {
        return;
    }
    var codeBlocks = container.getElementsByClassName("code-block");

    for (var i = 0; i < codeBlocks.length; i++) {
        var codeBlock = codeBlocks[i];

        var codeElements = codeBlock.getElementsByTagName("code");
        if (codeElements.length > 0) {
            var codeElement = codeElements[0];

            // Already highlighted.
            if (codeElement.dataset.highlighted) {
                continue;
            }
            // No language specified. Don't try to auto detect.
            if (!codeElement.dataset.language) {
                continue;
            }

            hljs.highlightElement(codeElement);
        }
    }
}
