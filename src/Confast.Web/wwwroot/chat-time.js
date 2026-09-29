window.confastChatTime = {
    conversationScrollPositions: new Map(),
    lastInsertedMention: null,
    attachSidebarResize(userId) {
        const body = document.querySelector(".chat-panel-body");
        const edge = body?.querySelector(".chat-sidebar-resize-edge");
        if (!body || !edge) return false;
        this.stopSidebarResize();

        const minimum = 200;
        const storageKey = `confast.chat.sidebarWidth.${userId}`;
        let preferredWidth = 270;
        try {
            const saved = Number(localStorage.getItem(storageKey));
            if (Number.isFinite(saved) && saved >= minimum && saved <= 480) preferredWidth = saved;
        } catch { /* Resizing still works when browser storage is unavailable. */ }

        const maximum = () => {
            const members = body.querySelector(".chat-members-sidebar");
            const membersWidth = members && getComputedStyle(members).position !== "absolute"
                ? members.getBoundingClientRect().width : 0;
            return Math.max(minimum, Math.min(480, body.clientWidth - membersWidth - 200));
        };
        const applyWidth = width => {
            const max = maximum();
            const actual = Math.max(minimum, Math.min(max, Math.round(width)));
            body.style.setProperty("--chat-sidebar-width", `${actual}px`);
            edge.setAttribute("aria-valuenow", String(actual));
            edge.setAttribute("aria-valuemax", String(max));
            return actual;
        };
        const save = () => {
            try { localStorage.setItem(storageKey, String(preferredWidth)); }
            catch { /* Keep the width for this open chat session. */ }
        };
        let draggingPointerId = null;
        const onPointerDown = event => {
            if (event.button !== 0) return;
            event.preventDefault();
            draggingPointerId = event.pointerId;
            edge.setPointerCapture(event.pointerId);
            body.classList.add("chat-sidebar-resizing");
        };
        const onPointerMove = event => {
            if (event.pointerId !== draggingPointerId) return;
            preferredWidth = applyWidth(event.clientX - body.getBoundingClientRect().left);
        };
        const onPointerUp = event => {
            if (event.pointerId !== draggingPointerId) return;
            draggingPointerId = null;
            body.classList.remove("chat-sidebar-resizing");
            if (edge.hasPointerCapture(event.pointerId)) edge.releasePointerCapture(event.pointerId);
            save();
        };
        const onKeyDown = event => {
            const current = Number(edge.getAttribute("aria-valuenow"));
            const next = event.key === "ArrowLeft" ? current - 10
                : event.key === "ArrowRight" ? current + 10
                : event.key === "Home" ? minimum
                : event.key === "End" ? maximum() : null;
            if (next === null) return;
            event.preventDefault();
            preferredWidth = applyWidth(next);
            save();
        };
        const controller = new AbortController();
        edge.addEventListener("pointerdown", onPointerDown, { signal: controller.signal });
        edge.addEventListener("pointermove", onPointerMove, { signal: controller.signal });
        edge.addEventListener("pointerup", onPointerUp, { signal: controller.signal });
        edge.addEventListener("pointercancel", onPointerUp, { signal: controller.signal });
        edge.addEventListener("keydown", onKeyDown, { signal: controller.signal });
        window.addEventListener("resize", () => applyWidth(preferredWidth), { signal: controller.signal });
        const observer = new MutationObserver(() => applyWidth(preferredWidth));
        observer.observe(body, { childList: true });
        body.classList.add("chat-sidebar-resizing");
        applyWidth(preferredWidth);
        requestAnimationFrame(() => body.classList.remove("chat-sidebar-resizing"));
        this.sidebarResizeCleanup = () => {
            save();
            observer.disconnect();
            controller.abort();
            body.classList.remove("chat-sidebar-resizing");
        };
        return true;
    },
    stopSidebarResize() {
        this.sidebarResizeCleanup?.();
        this.sidebarResizeCleanup = null;
    },
    formatMessageTimes(utcValues) {
        const formatter = new Intl.DateTimeFormat(undefined, {
            month: "short",
            day: "numeric",
            hour: "numeric",
            minute: "2-digit"
        });
        return utcValues.map(value => formatter.format(new Date(value)));
    },
    formatMessageDates(utcValues) {
        const formatter = new Intl.DateTimeFormat(undefined, {
            weekday: "long",
            month: "long",
            day: "numeric",
            year: "numeric"
        });
        const timeFormatter = new Intl.DateTimeFormat(undefined, {
            hour: "numeric",
            minute: "2-digit"
        });
        const pad = value => String(value).padStart(2, "0");
        return utcValues.map(value => {
            const date = new Date(value);
            const key = `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`;
            return [key, formatter.format(date), timeFormatter.format(date)];
        });
    },
    formatActivityDates(utcValues) {
        const formatter = new Intl.DateTimeFormat(undefined, {
            month: "short",
            day: "numeric"
        });
        return utcValues.map(value => formatter.format(new Date(value)));
    },
    enableEnterToSend(id = "chat-message-body") {
        const textarea = document.getElementById(id);
        if (!(textarea instanceof HTMLTextAreaElement)) return false;
        if (textarea.getAttribute("data-enter-to-send-attached") === "true") return true;

        textarea.addEventListener("keydown", event => {
            if (id === "chat-message-body" && !event.isComposing) {
                const mentionMenu = document.getElementById("chat-mention-menu");
                if (mentionMenu && event.key === "Escape") {
                    event.preventDefault();
                    document.getElementById("chat-mention-dismiss")?.click();
                    return;
                }
                const options = mentionMenu ? [...mentionMenu.querySelectorAll("button[role='option']")] : [];
                if (options.length && (event.key === "ArrowDown" || event.key === "ArrowUp")) {
                    event.preventDefault();
                    const currentIndex = options.findIndex(option => option.getAttribute("aria-selected") === "true");
                    const nextIndex = currentIndex < 0
                        ? (event.key === "ArrowDown" ? 0 : options.length - 1)
                        : (currentIndex + (event.key === "ArrowDown" ? 1 : -1) + options.length) % options.length;
                    options.forEach((option, index) => option.setAttribute("aria-selected", index === nextIndex ? "true" : "false"));
                    options[nextIndex].scrollIntoView({ block: "nearest" });
                    return;
                }
                if (options.length && event.key === "Tab" && !event.shiftKey) {
                    event.preventDefault();
                    (options.find(option => option.getAttribute("aria-selected") === "true") ?? options[0]).click();
                    return;
                }
                if (options.length && event.key === "Enter" && !event.shiftKey) {
                    event.preventDefault();
                    (options.find(option => option.getAttribute("aria-selected") === "true") ?? options[0]).click();
                    return;
                }
            }
            if (event.key !== "Enter" || event.shiftKey || event.isComposing) return;
            event.preventDefault();
            textarea.form?.requestSubmit();
        });
        if (id === "chat-message-body") {
            textarea.addEventListener("compositionstart", () => textarea.dataset.chatComposing = "true");
            textarea.addEventListener("compositionend", () => delete textarea.dataset.chatComposing);
            textarea.addEventListener("input", () => this.syncDraftPreviewScroll());
            textarea.addEventListener("scroll", () => this.syncDraftPreviewScroll());
            textarea.addEventListener("pointerup", () => this.syncDraftPreviewScroll());
            this.syncDraftPreviewScroll();
        }
        textarea.setAttribute("data-enter-to-send-attached", "true");
        return true;
    },
    syncDraftPreviewScroll() {
        const textarea = document.getElementById("chat-message-body");
        const preview = document.getElementById("chat-draft-preview");
        if (!(textarea instanceof HTMLTextAreaElement) || !preview) return;
        const style = getComputedStyle(textarea);
        const scrollbarWidth = textarea.offsetWidth - textarea.clientWidth
            - parseFloat(style.borderLeftWidth) - parseFloat(style.borderRightWidth);
        preview.style.right = `${Math.max(0, scrollbarWidth)}px`;
        preview.scrollTop = textarea.scrollTop;
        preview.scrollLeft = textarea.scrollLeft;
    },
    normalizeDraftText(raw, normalized) {
        const textarea = document.getElementById("chat-message-body");
        if (!(textarea instanceof HTMLTextAreaElement) || textarea.dataset.chatComposing === "true"
            || textarea.value !== raw || raw.length !== normalized.length) return false;
        const start = textarea.selectionStart;
        const end = textarea.selectionEnd;
        textarea.value = normalized;
        textarea.setSelectionRange(start, end);
        this.syncDraftPreviewScroll();
        return true;
    },
    getMentionQuery(availableTags) {
        const textarea = document.getElementById("chat-message-body");
        if (!(textarea instanceof HTMLTextAreaElement) || textarea.selectionStart !== textarea.selectionEnd) return null;
        const cursor = textarea.selectionStart;
        const start = textarea.value.lastIndexOf("@", cursor - 1);
        if (start < 0 || cursor - start > 81) return null;
        if (start > 0 && /[\p{L}\p{N}_]/u.test(textarea.value[start - 1])) return null;
        const inserted = this.lastInsertedMention;
        if (inserted?.start === start && cursor >= start + inserted.text.length
            && textarea.value.startsWith(inserted.text, start)) return null;
        const query = textarea.value.slice(start + 1, cursor);
        if (/[\r\n@]/.test(query)) return null;
        if (query.includes(" ") && Array.isArray(availableTags)
            && !availableTags.some(tag => tag.toLowerCase().startsWith(query.toLowerCase()))) return null;
        return { Start: start, Query: query };
    },
    insertMention(start, tag) {
        const textarea = document.getElementById("chat-message-body");
        if (!(textarea instanceof HTMLTextAreaElement) || textarea.value[start] !== "@") return null;
        const cursor = textarea.selectionStart;
        const value = textarea.value.slice(0, start) + `@${tag} ` + textarea.value.slice(cursor);
        if (value.length > textarea.maxLength) return null;
        textarea.value = value;
        const nextCursor = start + tag.length + 2;
        textarea.focus();
        textarea.setSelectionRange(nextCursor, nextCursor);
        this.lastInsertedMention = { start, text: `@${tag} ` };
        this.syncDraftPreviewScroll();
        return value;
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
        const scroll = sidebar.querySelector(".chat-sidebar-scroll");
        const rows = () => Array.from(scroll.children)
            .filter(child => child.matches(".chat-layout-row[data-chat-item-kind]"));
        const parentOf = row => row.dataset.chatParent ? Number(row.dataset.chatParent) : null;
        const depthOf = row => Number(row.dataset.chatDepth);
        const folderName = parentId => rows().find(row =>
            row.dataset.chatItemKind === "folder" && Number(row.dataset.chatDragId) === parentId)
            ?.querySelector(".chat-folder-label strong")?.textContent.trim();
        const slot = (parentId, beforeRow, y, depth, bounds) => ({
            parentId,
            beforeIsFolder: beforeRow ? beforeRow.dataset.chatItemKind === "folder" : null,
            beforeId: beforeRow ? Number(beforeRow.dataset.chatDragId) : null,
            top: y,
            left: bounds.left + 4 + depth * 16,
            right: bounds.right - 4,
            label: parentId === null ? "Root level" : `Inside ${folderName(parentId) ?? "folder"}`
        });
        const dropSlot = (x, y, sourceRow) => {
            const bounds = scroll.getBoundingClientRect();
            if (x < bounds.left || x > bounds.right || y < bounds.top || y > bounds.bottom) return null;
            const all = rows();
            const element = document.elementFromPoint(x, y);
            const row = element?.closest(".chat-layout-row[data-chat-item-kind]");
            const rootZone = element?.closest("[data-chat-root-drop]");
            let result = null;
            if (rootZone && scroll.contains(rootZone)) {
                const first = all.find(item => depthOf(item) === 0);
                const rect = (first ?? rootZone).getBoundingClientRect();
                result = slot(null, first, first ? rect.top : rect.bottom, 0, bounds);
            } else if (row && scroll.contains(row) && row.dataset.chatDropDisabled !== "true") {
                const index = all.indexOf(row);
                const depth = depthOf(row);
                const rect = row.getBoundingClientRect();
                const fraction = (y - rect.top) / rect.height;
                if (row.dataset.chatItemKind === "folder" && fraction >= .3 && fraction <= .7) {
                    const next = all[index + 1];
                    const firstChild = next && depthOf(next) === depth + 1 &&
                        next.dataset.chatDropDisabled !== "true" ? next : null;
                    result = slot(Number(row.dataset.chatDragId), firstChild,
                        firstChild ? firstChild.getBoundingClientRect().top : rect.bottom, depth + 1, bounds);
                } else if (fraction < .5) {
                    result = slot(parentOf(row), row, rect.top, depth, bounds);
                } else {
                    let nextIndex = index + 1;
                    while (nextIndex < all.length && depthOf(all[nextIndex]) > depth) nextIndex++;
                    const next = all[nextIndex];
                    const nextSibling = next && depthOf(next) === depth ? next : null;
                    const lastInSubtree = all[nextIndex - 1];
                    result = slot(parentOf(row), nextSibling,
                        next ? next.getBoundingClientRect().top : lastInSubtree.getBoundingClientRect().bottom,
                        depth, bounds);
                }
            } else if (scroll.contains(element) && all.length && y >= all[all.length - 1].getBoundingClientRect().bottom) {
                result = slot(null, null, all[all.length - 1].getBoundingClientRect().bottom, 0, bounds);
            }
            if (!result || result.top < bounds.top || result.top > bounds.bottom) return null;
            if (sourceRow.dataset.chatDragKind === "folder") {
                const sourceId = Number(sourceRow.dataset.chatDragId);
                const sourceDepth = depthOf(sourceRow);
                const sourceIndex = all.indexOf(sourceRow);
                const descendants = new Set([sourceId]);
                for (let index = sourceIndex + 1; index < all.length && depthOf(all[index]) > sourceDepth; index++)
                    if (all[index].dataset.chatItemKind === "folder") descendants.add(Number(all[index].dataset.chatDragId));
                if (descendants.has(result.parentId)) return null;
            }
            return result;
        };

        sidebar.addEventListener("pointerdown", event => {
            if (event.button !== 0) return;
            if (event.target.closest(".chat-channel-settings, .chat-folder-rename, .chat-folder-add, .chat-channel-action-toggle")) return;
            const sourceRow = event.target.closest(".chat-layout-row[data-chat-drag-kind]");
            if (!sourceRow || !scroll.contains(sourceRow)) return;
            if (sourceRow.dataset.chatDragKind !== "folder" && sourceRow.dataset.chatDragKind !== "channel") return;
            const itemId = Number(sourceRow.dataset.chatDragId);
            const isFolder = sourceRow.dataset.chatDragKind === "folder";
            const pointerId = event.pointerId;
            const startX = event.clientX;
            const startY = event.clientY;
            const isTouch = event.pointerType === "touch";
            let moved = false;
            let scrolling = false;
            let previousY = startY;
            let preview = null;
            let insertionLine = null;
            let holdTimer = null;
            let currentSlot = null;

            const showSlot = pointerEvent => {
                currentSlot = dropSlot(pointerEvent.clientX, pointerEvent.clientY, sourceRow);
                if (!currentSlot) {
                    insertionLine.style.display = "none";
                    return;
                }
                insertionLine.style.display = "block";
                insertionLine.style.left = `${currentSlot.left}px`;
                insertionLine.style.top = `${currentSlot.top}px`;
                insertionLine.style.width = `${Math.max(8, currentSlot.right - currentSlot.left)}px`;
                insertionLine.dataset.label = currentSlot.label;
            };
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
                sidebar.classList.remove("chat-channel-dragging");
                sourceRow.classList.remove("chat-channel-drag-source");
                preview?.remove();
                insertionLine?.remove();
                if (sourceRow.hasPointerCapture?.(pointerId)) sourceRow.releasePointerCapture(pointerId);
            };
            const startDrag = pointerEvent => {
                moved = true;
                sourceRow.setPointerCapture?.(pointerId);
                sidebar.classList.add("chat-channel-dragging");
                sourceRow.classList.add("chat-channel-drag-source");
                preview = sourceRow.cloneNode(true);
                preview.querySelectorAll(".chat-channel-settings, .chat-channel-action-toggle, .chat-folder-chevron, .chat-folder-add").forEach(x => x.remove());
                preview.classList.add("chat-channel-drag-preview");
                preview.setAttribute("aria-hidden", "true");
                preview.style.width = `${sourceRow.getBoundingClientRect().width}px`;
                document.body.appendChild(preview);
                insertionLine = sourceRow.cloneNode(false);
                insertionLine.className = "chat-channel-insertion-line";
                insertionLine.removeAttribute("data-chat-drag-kind");
                insertionLine.removeAttribute("data-chat-item-kind");
                insertionLine.removeAttribute("data-chat-drag-id");
                insertionLine.removeAttribute("title");
                insertionLine.setAttribute("aria-hidden", "true");
                document.body.appendChild(insertionLine);
                positionPreview(pointerEvent);
                showSlot(pointerEvent);
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
                        scroll.scrollTop -= moveEvent.clientY - previousY;
                        previousY = moveEvent.clientY;
                    }
                    return;
                }
                if (!moved && Math.hypot(moveEvent.clientX - startX, moveEvent.clientY - startY) > 8)
                    startDrag(moveEvent);
                if (moved) {
                    moveEvent.preventDefault();
                    positionPreview(moveEvent);
                    showSlot(moveEvent);
                }
            };
            const onPointerUp = upEvent => {
                if (upEvent.pointerId !== pointerId) return;
                if (moved) showSlot(upEvent);
                const target = currentSlot;
                clear();
                if (moved || scrolling) {
                    const suppressClick = clickEvent => {
                        clickEvent.preventDefault();
                        clickEvent.stopPropagation();
                    };
                    document.addEventListener("click", suppressClick, { capture: true, once: true });
                    setTimeout(() => document.removeEventListener("click", suppressClick, true), 0);
                    if (moved && target)
                        dotNet.invokeMethodAsync("MoveLayoutFromDragAsync", isFolder, itemId,
                            target.parentId, target.beforeIsFolder, target.beforeId);
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
    positionMessageActions(messageId) {
        const menu = document.getElementById(`chat-message-more-${messageId}`);
        const trigger = document.querySelector(`[data-chat-more-trigger="${messageId}"]`);
        const panel = document.querySelector(".chat-panel");
        if (!menu || !trigger || !panel) return;
        const boundary = panel.getBoundingClientRect();
        const anchor = trigger.getBoundingClientRect();
        const margin = 8;
        const width = menu.offsetWidth;
        const height = menu.offsetHeight;
        const left = Math.max(boundary.left + margin,
            Math.min(anchor.right - width, boundary.right - width - margin));
        const below = anchor.bottom + 5;
        const above = anchor.top - height - 5;
        const top = below + height <= boundary.bottom - margin ? below
            : above >= boundary.top + margin ? above
            : Math.max(boundary.top + margin, boundary.bottom - height - margin);
        menu.style.left = `${Math.round(left)}px`;
        menu.style.top = `${Math.round(top)}px`;
        menu.style.visibility = "visible";
    },
    focusMessageActions(messageId) {
        document.getElementById(`chat-message-more-${messageId}`)?.querySelector("button")?.focus();
    },
    watchMessageActionsOutsideClick(dotNetReference) {
        if (this.messageActionsWatch) return;
        const dismiss = () => dotNetReference.invokeMethodAsync("DismissMessageActionsAsync");
        const onDown = event => {
            if (event.target instanceof Element && event.target.closest(
                ".chat-message-more-menu, .chat-more-action")) return;
            dismiss();
        };
        const onKey = event => {
            if (event.key === "Escape") {
                event.preventDefault();
                const trigger = document.querySelector(".chat-more-action[aria-expanded='true']");
                dismiss().then(() => trigger?.focus());
            }
        };
        const history = document.getElementById("chat-message-history");
        document.addEventListener("pointerdown", onDown, true);
        document.addEventListener("keydown", onKey, true);
        history?.addEventListener("scroll", dismiss, { passive: true });
        this.messageActionsWatch = () => {
            document.removeEventListener("pointerdown", onDown, true);
            document.removeEventListener("keydown", onKey, true);
            history?.removeEventListener("scroll", dismiss);
        };
    },
    stopWatchingMessageActionsOutsideClick() {
        this.messageActionsWatch?.();
        this.messageActionsWatch = null;
    },
    copyMessageText(text) {
        return navigator.clipboard.writeText(text);
    },
    positionChannelSettings(channelId) {
        const popover = document.getElementById(`chat-channel-settings-${channelId}`);
        const anchor = document.querySelector(`[data-chat-settings-toggle="${channelId}"]`);
        const panel = document.querySelector(".chat-panel");
        if (!popover || !anchor || !panel) return;
        const boundary = panel.getBoundingClientRect();
        const rect = anchor.getBoundingClientRect();
        const margin = 8;
        const width = popover.offsetWidth;
        const height = popover.offsetHeight;
        const right = rect.right + 6;
        const left = rect.left - width - 6;
        const x = right + width <= boundary.right - margin ? right
            : left >= boundary.left + margin ? left
            : Math.max(boundary.left + margin, boundary.right - width - margin);
        const y = Math.max(boundary.top + margin,
            Math.min(rect.top, boundary.bottom - height - margin));
        popover.style.left = `${Math.round(x)}px`;
        popover.style.top = `${Math.round(y)}px`;
        popover.style.visibility = "visible";
    },
    jumpToMessage(messageId) {
        const history = document.getElementById("chat-message-history");
        const message = Array.from(history?.querySelectorAll("[data-chat-message-id]") ?? [])
            .find(element => element.dataset.chatMessageId === String(messageId));
        if (!history || !message) return false;
        message.scrollIntoView({ block: "center", behavior: "smooth" });
        message.classList.remove("chat-reply-highlight");
        void message.offsetWidth;
        message.classList.add("chat-reply-highlight");
        setTimeout(() => message.classList.remove("chat-reply-highlight"), 1800);
        return true;
    },
    watchChannelSettingsOutsideClick(dotNetReference) {
        if (this.channelSettingsOutsideClick) return;
        this.channelSettingsOutsideClick = event => {
            if (event.target instanceof Element && event.target.closest(".chat-channel-settings, .chat-channel-action-toggle")) return;
            dotNetReference.invokeMethodAsync("DismissChannelSettingsAsync");
        };
        this.channelSettingsEscape = event => {
            if (event.key === "Escape") dotNetReference.invokeMethodAsync("DismissChannelSettingsAsync");
        };
        this.channelSettingsScroll = event => {
            if (event.target instanceof Element && event.target.closest(".chat-channel-settings")) return;
            dotNetReference.invokeMethodAsync("DismissChannelSettingsAsync");
        };
        document.addEventListener("pointerdown", this.channelSettingsOutsideClick, true);
        document.addEventListener("keydown", this.channelSettingsEscape, true);
        document.addEventListener("scroll", this.channelSettingsScroll, true);
    },
    stopWatchingChannelSettingsOutsideClick() {
        if (!this.channelSettingsOutsideClick) return;
        document.removeEventListener("pointerdown", this.channelSettingsOutsideClick, true);
        document.removeEventListener("keydown", this.channelSettingsEscape, true);
        document.removeEventListener("scroll", this.channelSettingsScroll, true);
        this.channelSettingsOutsideClick = null;
        this.channelSettingsEscape = null;
        this.channelSettingsScroll = null;
    },
    watchPinnedMessagesOutsideClick(dotNetReference) {
        if (this.pinnedMessagesWatch) return;
        const dismiss = () => dotNetReference.invokeMethodAsync("DismissPinnedMessagesAsync");
        const onDown = event => {
            if (!document.getElementById("chat-pinned-menu")) return;
            if (event.target instanceof Element && event.target.closest(
                ".chat-pinned-control, [data-chat-pinned-trigger]")) return;
            dismiss();
        };
        const onKey = event => {
            if (event.key !== "Escape" || !document.getElementById("chat-pinned-menu")) return;
            event.preventDefault();
            dismiss().then(() => document.querySelector(".chat-pinned-control > button")?.focus());
        };
        document.addEventListener("pointerdown", onDown, true);
        document.addEventListener("keydown", onKey, true);
        this.pinnedMessagesWatch = () => {
            document.removeEventListener("pointerdown", onDown, true);
            document.removeEventListener("keydown", onKey, true);
        };
    },
    stopWatchingPinnedMessagesOutsideClick() {
        this.pinnedMessagesWatch?.();
        this.pinnedMessagesWatch = null;
    },
    positionMessageProfile(anchorKey) {
        const popover = document.querySelector(".chat-user-popover");
        const anchor = document.querySelector(`[data-chat-profile-anchor="${anchorKey}"]`);
        if (!popover || !anchor) return;
        const panel = document.querySelector(".chat-panel");
        const boundary = panel?.getBoundingClientRect() ?? { left: 0, right: window.innerWidth, top: 0, bottom: window.innerHeight };
        const rect = anchor.getBoundingClientRect();
        const margin = 8;
        const width = popover.offsetWidth;
        const height = popover.offsetHeight;
        const left = Math.max(boundary.left + margin, Math.min(rect.left, boundary.right - width - margin));
        const below = rect.bottom + 6;
        const above = rect.top - height - 6;
        const top = below + height <= boundary.bottom - margin ? below
            : above >= boundary.top + margin ? above
            : Math.max(boundary.top + margin, boundary.bottom - height - margin);
        popover.style.left = `${Math.round(left)}px`;
        popover.style.top = `${Math.round(top)}px`;
        popover.style.visibility = "visible";
    },
    watchMessageProfileOutsideClick(dotNetReference) {
        if (this.messageProfileOutsideClick) return;
        this.messageProfileOutsideClick = event => {
            if (event.target instanceof Element && event.target.closest(".chat-user-popover, .chat-sender-link, .chat-message-user-tag")) return;
            dotNetReference.invokeMethodAsync("DismissMessageProfileAsync");
        };
        this.messageProfileEscape = event => {
            if (event.key === "Escape") dotNetReference.invokeMethodAsync("DismissMessageProfileAsync");
        };
        document.addEventListener("pointerdown", this.messageProfileOutsideClick, true);
        document.addEventListener("keydown", this.messageProfileEscape, true);
    },
    stopWatchingMessageProfileOutsideClick() {
        if (!this.messageProfileOutsideClick) return;
        document.removeEventListener("pointerdown", this.messageProfileOutsideClick, true);
        document.removeEventListener("keydown", this.messageProfileEscape, true);
        this.messageProfileOutsideClick = null;
        this.messageProfileEscape = null;
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
    watchQuickTonePicker(dotNetReference) {
        if (this.quickTonePickerWatch) return;
        let dismissTimer;
        const isInside = target => {
            if (!(target instanceof Element)) return false;
            if (target.closest(".chat-tone-picker")) return true;
            const anchor = target.closest("[data-tone-message][data-tone-base][data-tone-source]");
            const current = this.tonePickerAnchor;
            return !!anchor && !!current
                && anchor.dataset.toneMessage === String(current.messageId)
                && anchor.dataset.toneBase === current.defaultEmoji
                && anchor.dataset.toneSource === current.source;
        };
        const onMove = event => {
            clearTimeout(dismissTimer);
            if (!isInside(event.target))
                dismissTimer = setTimeout(() => dotNetReference.invokeMethodAsync("DismissQuickTonePickerAsync"), 180);
        };
        const onDown = event => {
            if (!isInside(event.target))
                dotNetReference.invokeMethodAsync("DismissQuickTonePickerAsync");
        };
        document.addEventListener("pointermove", onMove, true);
        document.addEventListener("pointerdown", onDown, true);
        window.addEventListener("blur", onDown);
        this.quickTonePickerWatch = () => {
            clearTimeout(dismissTimer);
            document.removeEventListener("pointermove", onMove, true);
            document.removeEventListener("pointerdown", onDown, true);
            window.removeEventListener("blur", onDown);
        };
    },
    stopWatchingQuickTonePicker() {
        this.quickTonePickerWatch?.();
        this.quickTonePickerWatch = null;
        this.tonePickerAnchor = null;
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
