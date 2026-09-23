window.confastChatTime = {
    conversationScrollPositions: new Map(),
    formatMessageTimes(utcValues) {
        const formatter = new Intl.DateTimeFormat(undefined, {
            month: "short",
            day: "numeric",
            hour: "numeric",
            minute: "2-digit",
            timeZoneName: "short"
        });
        return utcValues.map(value => formatter.format(new Date(value)));
    },
    formatActivityDates(utcValues) {
        const formatter = new Intl.DateTimeFormat(undefined, {
            month: "short",
            day: "numeric"
        });
        return utcValues.map(value => formatter.format(new Date(value)));
    },
    enableEnterToSend() {
        const textarea = document.getElementById("chat-message-body");
        if (!(textarea instanceof HTMLTextAreaElement)) return false;
        if (textarea.getAttribute("data-enter-to-send-attached") === "true") return true;

        textarea.addEventListener("keydown", event => {
            if (event.key !== "Enter" || event.shiftKey || event.isComposing) return;
            event.preventDefault();
            textarea.form?.requestSubmit();
        });
        textarea.setAttribute("data-enter-to-send-attached", "true");
        return true;
    },
    scrollToLatestMessage() {
        const history = document.getElementById("chat-message-history");
        if (!history) return;
        history.scrollTop = history.scrollHeight;
        const conversationId = history.getAttribute("data-conversation-id");
        if (conversationId) {
            window.confastChatTime.conversationScrollPositions.set(conversationId, history.scrollTop);
        }
    },
    restoreConversationScrollPosition() {
        const history = document.getElementById("chat-message-history");
        if (!history) return;

        const conversationId = history.getAttribute("data-conversation-id");
        if (!conversationId) return;

        const scrollPositions = window.confastChatTime.conversationScrollPositions;
        const savedPosition = scrollPositions.get(conversationId);
        history.scrollTop = savedPosition ?? history.scrollHeight;
        scrollPositions.set(conversationId, history.scrollTop);

        if (history.getAttribute("data-scroll-tracking-attached") !== "true") {
            history.addEventListener("scroll", () => {
                const currentConversationId = history.getAttribute("data-conversation-id");
                if (currentConversationId) {
                    scrollPositions.set(currentConversationId, history.scrollTop);
                }
            }, { passive: true });
            history.setAttribute("data-scroll-tracking-attached", "true");
        }
    }
};
