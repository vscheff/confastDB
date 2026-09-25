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
    isNearLatestMessage() {
        const history = document.getElementById("chat-message-history");
        return !!history && history.scrollHeight - history.clientHeight - history.scrollTop <= 4;
    },
    updateNewMessageJump() {
        const button = document.querySelector(".chat-new-message-jump");
        if (button) button.hidden = window.confastChatTime.isNearLatestMessage();
    },
    jumpToNewMessage() {
        const button = document.querySelector(".chat-new-message-jump");
        window.confastChatTime.scrollToLatestMessage();
        if (button) button.hidden = true;
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
                window.confastChatTime.positionReactionPicker();
                window.confastChatTime.updateNewMessageJump();
            }, { passive: true });
            history.setAttribute("data-scroll-tracking-attached", "true");
        }
    },
    attachChannelDragAndDrop(dotNet) {
        const sidebar = document.getElementById("chat-conversation-sidebar");
        if (!sidebar) return false;
        if (sidebar.dataset.channelDragAttached === "true") return true;

        let highlighted = null;
        const dropTarget = element => {
            const target = element?.closest("[data-channel-drop-group]");
            return target && sidebar.contains(target) ? target : null;
        };
        const highlight = target => {
            if (highlighted === target) return;
            highlighted?.classList.remove("chat-drop-target");
            highlighted = target;
            highlighted?.classList.add("chat-drop-target");
        };
        const move = (channelId, target) => {
            if (!target) return;
            const groupValue = target.getAttribute("data-channel-drop-group");
            const beforeValue = target.getAttribute("data-channel-drop-before");
            const groupId = groupValue ? Number(groupValue) : null;
            const beforeId = beforeValue ? Number(beforeValue) : null;
            dotNet.invokeMethodAsync("MoveChannelFromDragAsync", Number(channelId), groupId, beforeId);
        };
        sidebar.addEventListener("pointerdown", event => {
            if (event.button !== 0) return;
            if (event.target.closest(".chat-channel-controls")) return;
            const sourceRow = event.target.closest(".chat-channel-row[data-chat-drag-channel]");
            if (!sourceRow || !sidebar.contains(sourceRow)) return;
            const channelId = sourceRow.getAttribute("data-chat-drag-channel");
            const pointerId = event.pointerId;
            const startX = event.clientX;
            const startY = event.clientY;
            const isTouch = event.pointerType === "touch";
            let moved = false;
            let scrolling = false;
            let previousY = startY;
            let preview = null;
            let holdTimer = null;

            const positionPreview = pointerEvent => {
                const width = preview.offsetWidth;
                const height = preview.offsetHeight;
                preview.style.left = `${Math.max(8, Math.min(pointerEvent.clientX + 12, window.innerWidth - width - 8))}px`;
                preview.style.top = `${Math.max(8, Math.min(pointerEvent.clientY + 12, window.innerHeight - height - 8))}px`;
            };

            const clear = () => {
                if (holdTimer) clearTimeout(holdTimer);
                document.removeEventListener("pointermove", onPointerMove);
                document.removeEventListener("pointerup", onPointerUp);
                document.removeEventListener("pointercancel", onPointerCancel);
                highlight(null);
                sidebar.classList.remove("chat-channel-dragging");
                sourceRow.classList.remove("chat-channel-drag-source");
                preview?.remove();
                if (sourceRow.hasPointerCapture?.(pointerId)) sourceRow.releasePointerCapture(pointerId);
            };
            const startDrag = pointerEvent => {
                moved = true;
                sourceRow.setPointerCapture?.(pointerId);
                sidebar.classList.add("chat-channel-dragging");
                sourceRow.classList.add("chat-channel-drag-source");
                preview = sourceRow.cloneNode(true);
                preview.querySelector(".chat-channel-controls")?.remove();
                preview.classList.add("chat-channel-drag-preview");
                preview.setAttribute("aria-hidden", "true");
                preview.style.width = `${sourceRow.getBoundingClientRect().width}px`;
                document.body.appendChild(preview);
                positionPreview(pointerEvent);
            };
            const onPointerMove = moveEvent => {
                if (moveEvent.pointerId !== pointerId) return;
                if (isTouch && !moved) {
                    if (!scrolling && Math.hypot(moveEvent.clientX - startX, moveEvent.clientY - startY) > 8) {
                        scrolling = true;
                        clearTimeout(holdTimer);
                    }
                    if (scrolling) {
                        moveEvent.preventDefault();
                        sidebar.scrollTop -= moveEvent.clientY - previousY;
                        previousY = moveEvent.clientY;
                    }
                    return;
                }
                if (!moved && Math.hypot(moveEvent.clientX - startX, moveEvent.clientY - startY) > 8)
                    startDrag(moveEvent);
                if (moved) {
                    moveEvent.preventDefault();
                    positionPreview(moveEvent);
                    highlight(dropTarget(document.elementFromPoint(moveEvent.clientX, moveEvent.clientY)));
                }
            };
            const onPointerUp = upEvent => {
                if (upEvent.pointerId !== pointerId) return;
                const target = moved ? dropTarget(document.elementFromPoint(upEvent.clientX, upEvent.clientY)) : null;
                clear();
                if (moved || scrolling) {
                    const suppressClick = clickEvent => {
                        clickEvent.preventDefault();
                        clickEvent.stopPropagation();
                    };
                    document.addEventListener("click", suppressClick, { capture: true, once: true });
                    setTimeout(() => document.removeEventListener("click", suppressClick, true), 0);
                    if (target) move(channelId, target);
                }
            };
            const onPointerCancel = cancelEvent => {
                if (cancelEvent.pointerId === pointerId) clear();
            };

            document.addEventListener("pointermove", onPointerMove);
            document.addEventListener("pointerup", onPointerUp);
            document.addEventListener("pointercancel", onPointerCancel);
            if (isTouch) holdTimer = setTimeout(() => {
                if (!scrolling) startDrag({ clientX: startX, clientY: startY });
            }, 400);
        });
        sidebar.dataset.channelDragAttached = "true";
        return true;
    },
    positionReactionPicker() {
        const picker = document.querySelector(".chat-message.picker-open .chat-emoji-picker");
        const actions = document.querySelector(".chat-message.picker-open .chat-reaction-actions");
        const history = document.getElementById("chat-message-history");
        if (!picker || !actions || !history) return;

        const boundary = history.getBoundingClientRect();
        const anchor = actions.getBoundingClientRect();
        const margin = 8;
        if (anchor.bottom < boundary.top || anchor.top > boundary.bottom) {
            picker.style.visibility = "hidden";
            return;
        }
        picker.style.width = `${Math.max(0, Math.min(340, boundary.width - 2 * margin))}px`;
        picker.style.maxHeight = `${Math.max(100, boundary.height - 2 * margin)}px`;
        const width = picker.offsetWidth;
        const height = picker.offsetHeight;
        const left = Math.max(boundary.left + margin,
            Math.min(anchor.right - width, boundary.right - width - margin));
        const below = anchor.bottom + 6;
        const above = anchor.top - height - 6;
        const top = below + height <= boundary.bottom - margin ? below
            : above >= boundary.top + margin ? above
            : Math.max(boundary.top + margin, boundary.bottom - height - margin);
        picker.style.left = `${Math.round(left)}px`;
        picker.style.top = `${Math.round(top)}px`;
        picker.style.visibility = "visible";

        if (!window.confastChatTime.reactionPickerResizeAttached) {
            window.addEventListener("resize", () => window.confastChatTime.positionReactionPicker());
            window.confastChatTime.reactionPickerResizeAttached = true;
        }
    },
    watchReactionPickerOutsideClick(dotNetReference) {
        if (this.reactionPickerOutsideClick) return;
        this.reactionPickerOutsideClick = event => {
            const target = event.target;
            if (target instanceof Element && target.closest(
                ".chat-emoji-picker, .chat-reaction-more, .chat-tone-picker")) return;
            dotNetReference.invokeMethodAsync("DismissReactionPickerAsync");
        };
        document.addEventListener("pointerdown", this.reactionPickerOutsideClick, true);
    },
    stopWatchingReactionPickerOutsideClick() {
        if (!this.reactionPickerOutsideClick) return;
        document.removeEventListener("pointerdown", this.reactionPickerOutsideClick, true);
        this.reactionPickerOutsideClick = null;
    },
    watchProfileEditorOutsideClick(dotNetReference) {
        if (this.profileEditorOutsideClick) return;
        this.profileEditorOutsideClick = event => {
            const target = event.target;
            if (target instanceof Element && target.closest(".chat-presence-editor, .chat-profile-dock, .chat-picture-edit-backdrop")) return;
            dotNetReference.invokeMethodAsync("DismissPresenceEditorAsync");
        };
        document.addEventListener("pointerdown", this.profileEditorOutsideClick, true);
    },
    stopWatchingProfileEditorOutsideClick() {
        if (!this.profileEditorOutsideClick) return;
        document.removeEventListener("pointerdown", this.profileEditorOutsideClick, true);
        this.profileEditorOutsideClick = null;
    },
    watchStatusEmojiPickerOutsideClick(dotNetReference) {
        if (this.statusEmojiPickerOutsideClick) return;
        this.statusEmojiPickerOutsideClick = event => {
            const target = event.target;
            if (target instanceof Element && target.closest(".chat-status-emoji-picker, .chat-status-emoji-button")) return;
            dotNetReference.invokeMethodAsync("DismissStatusEmojiPickerAsync");
        };
        document.addEventListener("pointerdown", this.statusEmojiPickerOutsideClick, true);
    },
    stopWatchingStatusEmojiPickerOutsideClick() {
        if (!this.statusEmojiPickerOutsideClick) return;
        document.removeEventListener("pointerdown", this.statusEmojiPickerOutsideClick, true);
        this.statusEmojiPickerOutsideClick = null;
    },
    positionTonePicker(messageId, defaultEmoji, source) {
        const picker = document.querySelector(".chat-tone-picker");
        if (!picker) return;
        const anchor = [...document.querySelectorAll("[data-tone-message][data-tone-base][data-tone-source]")]
            .find(element => element.dataset.toneMessage === String(messageId)
                && element.dataset.toneBase === defaultEmoji && element.dataset.toneSource === source);
        if (!anchor) return;
        const rect = anchor.getBoundingClientRect();
        const margin = 8;
        const width = picker.offsetWidth;
        const height = picker.offsetHeight;
        const left = Math.max(margin, Math.min(rect.left, window.innerWidth - width - margin));
        const above = rect.top - height - 5;
        const below = rect.bottom + 5;
        const top = above >= margin ? above : Math.min(below, window.innerHeight - height - margin);
        picker.style.left = `${Math.round(left)}px`;
        picker.style.top = `${Math.round(Math.max(margin, top))}px`;
        picker.style.visibility = "visible";
        window.confastChatTime.tonePickerAnchor = { messageId, defaultEmoji, source };
        if (!window.confastChatTime.tonePickerTrackingAttached) {
            const reposition = () => {
                const current = window.confastChatTime.tonePickerAnchor;
                if (current && document.querySelector(".chat-tone-picker"))
                    window.confastChatTime.positionTonePicker(current.messageId, current.defaultEmoji, current.source);
            };
            document.addEventListener("scroll", reposition, true);
            window.addEventListener("resize", reposition);
            window.confastChatTime.tonePickerTrackingAttached = true;
        }
    },
    focusTonePicker() {
        document.querySelector(".chat-tone-picker button.selected")?.focus();
    },
    detectEmojiSupport(candidates) {
        try {
            const canvas = document.createElement("canvas");
            canvas.width = canvas.height = 96;
            const context = canvas.getContext("2d", { willReadFrequently: true });
            if (!context) throw new Error("Canvas is unavailable.");
            const fontFamily = '"Twemoji Mozilla","Apple Color Emoji","Segoe UI Emoji","Noto Color Emoji",sans-serif';
            context.font = `64px ${fontFamily}`;
            context.textBaseline = "top";

            const hasColorGlyph = emoji => {
                const render = color => {
                    context.clearRect(0, 0, 96, 96);
                    context.fillStyle = color;
                    context.fillText(emoji, 2, 2);
                    return context.getImageData(0, 0, 96, 96).data;
                };
                const black = render("#000");
                const white = render("#fff");
                let stableColorPixels = 0;
                for (let i = 0; i < black.length; i += 4) {
                    if (black[i + 3] < 32 || black[i] + black[i + 1] + black[i + 2] < 48) continue;
                    if (black[i] === white[i] && black[i + 1] === white[i + 1]
                        && black[i + 2] === white[i + 2] && black[i + 3] === white[i + 3]) {
                        if (++stableColorPixels >= 12) return true;
                    }
                }
                return false;
            };

            const probes = [
                ["🫫", 18], ["🫪", 17], ["🫩", 16], ["🫨", 15],
                ["🫠", 14], ["🥲", 13], ["🥻", 12], ["🥰", 11],
                ["🤩", 5], ["🤣", 3], ["😀", 1], ["😃", 0.6]
            ];
            let maxVersion = 18;
            if (hasColorGlyph("😀")) {
                maxVersion = probes.find(([emoji]) => hasColorGlyph(emoji))?.[1] ?? 0.6;
            }

            const baseWidth = context.measureText("😀").width;
            const isJoinedGlyph = emoji => baseWidth > 0 && context.measureText(emoji).width < baseWidth * 1.8;
            if (maxVersion === 15 && isJoinedGlyph("🙂‍↔️")) maxVersion = 15.1;
            if (maxVersion === 13 && isJoinedGlyph("😶‍🌫️")) maxVersion = 13.1;

            const unsupportedEmoji = [];
            for (const emoji of candidates) {
                const codePoints = [...emoji].map(character => character.codePointAt(0));
                const regionalFlag = codePoints.length === 2 &&
                    codePoints.every(codePoint => codePoint >= 0x1F1E6 && codePoint <= 0x1F1FF);
                const tagFlag = codePoints[0] === 0x1F3F4 && codePoints.some(codePoint => codePoint >= 0xE0061);
                if ((emoji.includes("\u200d") && !isJoinedGlyph(emoji)) ||
                    ((regionalFlag || tagFlag) && !hasColorGlyph(emoji))) {
                    unsupportedEmoji.push(emoji);
                }
            }
            return { maxVersion, unsupportedEmoji };
        } catch {
            // Canvas may be blocked by a privacy setting; preserve the full picker in that case.
            return { maxVersion: 18, unsupportedEmoji: [] };
        }
    }
};
