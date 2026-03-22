import hljs from '/js/highlight-11.11.1.min.js'

export function initializeAssistantChat(options) {
    const container = document.getElementById(options.containerId);

    if (container.assistantChat) {
        console.log("Assistant chat already created.");
        return;
    }

    const assistantChat = new AssistantChat(options);
    container.assistantChat = assistantChat;

    assistantChat.initializeExistingMessages();
    assistantChat.attachTextareaKeyDownEvent();
}

export function initializeCurrentMessage(options) {
    const container = document.getElementById(options.containerId);
    const assistantChat = container.assistantChat;

    if (!assistantChat) {
        console.log("Assistant chat not initialized.");
        return;
    }

    assistantChat.initializeCurrentMessage(options.chatMessageId);
}

export function completeCurrentMessage(options) {
    const container = document.getElementById(options.containerId);
    const assistantChat = container.assistantChat;

    if (!assistantChat) {
        console.log("Assistant chat not initialized.");
        return;
    }

    assistantChat.completeCurrentMessage(options.chatMessageId);
}

export function scrollToBottomChatAssistant(options) {
    const container = document.getElementById(options.containerId);
    const assistantChat = container.assistantChat;

    if (!assistantChat) {
        console.log("Assistant chat not initialized.");
        return;
    }

    assistantChat.scrollToBottom();
}

class AssistantChat {
    constructor(options) {
        this.chatAssistantContainer = document.getElementById(options.containerId);
        this.scrollBottomButton = document.getElementById(options.scrollBottomButtonId);
        this.form = document.getElementById(options.formId);
        this.textarea = document.getElementById(options.textareaId);
        this.messageCount = 0;

        this.chatAssistantContainer.addEventListener("scroll", () => {
            this.updateScrollTimeout();
            this.checkScrollPosition();
        });

        // Watch for both the chat area resizing and its content changing.
        var resizeObserver = new ResizeObserver(() => {
            this.checkScrollPosition();
        });
        resizeObserver.observe(this.chatAssistantContainer);
        var mutationObserver = new MutationObserver((r) => {
            this.checkScrollPosition();
        });
        mutationObserver.observe(this.chatAssistantContainer, { childList: true, subtree: true, characterData: true });

        this.scrollBottomButton.addEventListener("click", () => this.scrollToBottom());
    }

    scrollToBottom() {
        this.scrollBottomButton.style.display = 'none';
        this.chatAssistantContainer.scrollTop = this.chatAssistantContainer.scrollHeight;

        // Because the scroll behavior is smooth, the element isn't immediately scrolled to the bottom.
        // Detect when the element is scrolling and don't raise events until it is finished.
        this.scrollingToBottom = true;
        this.updateScrollTimeout();
    }

    updateScrollTimeout() {
        if (this.scrollTimeout) {
            // Cancel previous timer.
            clearTimeout(this.scrollTimeout);
        }

        // Set a new timer. The element has finished scrolling when the timer passes.
        this.scrollTimeout = setTimeout(() => {
            this.scrollingToBottom = false;
            this.checkScrollPosition();
        }, 150);
    }

    checkScrollPosition() {
        if (this.scrollingToBottom) {
            return;
        }

        var scrollableDiv = this.chatAssistantContainer;

        const isScrollable = scrollableDiv.scrollHeight > scrollableDiv.clientHeight;
        const isAtBottom = scrollableDiv.scrollTop + scrollableDiv.clientHeight >= scrollableDiv.scrollHeight - 10;

        // The scroll to bottom button is displayed if:
        // - There is a scroll bar
        // - We're not scrolled to the bottom
        // - There are messages (i.e. we're not on the splash view)
        if (isScrollable && !isAtBottom && this.messageCount > 0) {
            this.scrollBottomButton.style.display = '';
        } else {
            this.scrollBottomButton.style.display = 'none';
        }
    }

    initializeExistingMessages() {
        // Highlight code blocks in existing messages.
        var chatMessageElements = this.chatAssistantContainer.getElementsByClassName("assistant-message");

        var chatMessageElement = null;
        for (var i = 0; i < chatMessageElements.length; i++) {
            chatMessageElement = chatMessageElements[i];
            this.highlightCodeBlocks(chatMessageElement);
            this.messageCount++;
        }

        this.reactToNextStepsSize(chatMessageElement);
    }

    initializeCurrentMessage(chatMessageId) {
        // New message has started. Stop observing the old message.
        if (this.observer) {
            this.observer.disconnect();
        }

        this.messageCount++;

        // Follow up messages have been hidden so reset scroll to bottom button.
        this.scrollBottomButton.style.display = 'none';
        this.scrollBottomButton.style.setProperty("--next-steps-height", `0px`);

        const chatMessageElement = document.getElementById(chatMessageId);
        if (!chatMessageElement) {
            console.log(`Couldn't find ${chatMessageId}.`);
            return;
        }

        // Watch chat message for changes and highlight code blocks.
        // We're doing this in the client rather than via a Blazor invoke to avoid delay between HTML changing
        // and the code blocks being highlighted.
        this.observer = new MutationObserver((mutationsList, observer) => {
            for (let mutation of mutationsList) {
                if (mutation.type === "childList" || mutation.type === "characterData") {
                    this.highlightCodeBlocks(mutation.target);
                }
            }
        });

        const config = { childList: true, subtree: true, characterData: true };
        this.observer.observe(chatMessageElement, config);
    }

    completeCurrentMessage(chatMessageId) {
        var chatMessage = document.getElementById(chatMessageId);

        // Run client logic when the assistant message is finished returning...
        if (chatMessage.classList.contains("assistant-message")) {
            // Focus the text area for entering the next message.
            this.textarea.focus();

            this.reactToNextStepsSize(chatMessage);
        }
    }

    reactToNextStepsSize(lastChatMessage) {
        // Get the height of the next steps area and subtract from the min height of the previous message.
        // This prevents the next steps being added to the UI from pushing the previous message up.
        var nextSteps = document.getElementsByClassName("chat-assistant-next-steps")[0];
        if (nextSteps) {
            if (lastChatMessage && lastChatMessage.classList.contains("last-message")) {
                lastChatMessage.style.setProperty("--next-steps-height", `${nextSteps.clientHeight}px`);
            }

            this.scrollBottomButton.style.setProperty("--next-steps-height", `${nextSteps.clientHeight}px`);
        }

        // Update check of scroll position after sizes are adjusted.
        this.checkScrollPosition();
    }

    highlightCodeBlocks(chatMessageElement) {
        var codeBlocks = chatMessageElement.getElementsByClassName("code-block");

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

    attachTextareaKeyDownEvent() {
        this.textarea.addEventListener('input', () => this.resizeToFit(this.textarea));
        this.afterPropertyWritten(this.textarea, 'value', () => this.resizeToFit(this.textarea));

        this.resizeToFit(this.textarea);
        this.textarea.focus();

        var previousHasValue = this.textarea.value != '';
        this.textarea.addEventListener("keydown", (event) => {
            // Only send message to the server if the enter key is pressed.
            // Allow enter+shift to add a new line in the textarea.
            if (event.key === "Enter" && !event.shiftKey) {
                // Prevents newline insertion.
                event.preventDefault();

                // Don't submit form with enter if a response is in progress
                var responseInProgress = this.textarea.dataset.responseInProgress === "true";
                if (!responseInProgress) {
                    // Blazor listens on the change event to bind the value.
                    this.textarea.dispatchEvent(new CustomEvent('change', { bubbles: true }));
                    // Submit form.
                    this.form.dispatchEvent(new CustomEvent('submit', { bubbles: true }));
                } else {
                    console.log("Enter ignored because response is in progress.");
                }
            } else {
                setTimeout(() => {
                    var hasValue = this.textarea.value != '';
                    if (previousHasValue != hasValue) {
                        this.textarea.dispatchEvent(new CustomEvent('change', { bubbles: true }));
                        previousHasValue = hasValue;
                    }
                }, 0);
            }
        });
    }

    resizeToFit(elem) {
        const lineHeight = parseFloat(getComputedStyle(elem).lineHeight);

        elem.rows = 1;
        const numLines = Math.ceil(elem.scrollHeight / lineHeight);
        elem.rows = Math.min(5, Math.max(1, numLines));
    }

    afterPropertyWritten(target, propName, callback) {
        const descriptor = this.getPropertyDescriptor(target, propName);
        Object.defineProperty(target, propName, {
            get: function () {
                return descriptor.get.apply(this, arguments);
            },
            set: function () {
                const result = descriptor.set.apply(this, arguments);
                callback();
                return result;
            }
        });
    }

    getPropertyDescriptor(target, propertyName) {
        return Object.getOwnPropertyDescriptor(target, propertyName)
            || this.getPropertyDescriptor(Object.getPrototypeOf(target), propertyName);
    }
}
