export function focus(id) {
    document.getElementById(id)?.focus({ preventScroll: true });
}

window.addEventListener("resize", () => close(document.querySelector(".shortcut-menu[open]")));

export function show(dialog, openerId) {
    if (!dialog?.isConnected || dialog.open) return;
    const opener = document.getElementById(openerId);
    focus(openerId);
    dialog.showModal();

    const listeners = new AbortController();
    const options = { signal: listeners.signal };
    if (opener && dialog.classList.contains("shortcut-menu")) {
        const anchor = opener.getBoundingClientRect();
        const { width, height } = dialog.getBoundingClientRect();
        const edge = 8;
        const gap = 4;
        const below = anchor.bottom + gap;
        const left = Math.min(innerWidth - width - edge, Math.max(edge, anchor.right - width));
        const top = below + height <= innerHeight - edge ? below : Math.max(edge, anchor.top - height - gap);
        dialog.style.left = `${left}px`;
        dialog.style.top = `${top}px`;
    }

    let backdropPressed = false;
    function isBackdrop(event) {
        if (event.target !== dialog) return false;
        const bounds = dialog.getBoundingClientRect();
        return event.clientX < bounds.left || event.clientX > bounds.right
            || event.clientY < bounds.top || event.clientY > bounds.bottom;
    }
    dialog.addEventListener("pointerdown", event => { backdropPressed = isBackdrop(event); }, options);
    dialog.addEventListener("click", event => {
        if (backdropPressed && isBackdrop(event)) close(dialog);
        backdropPressed = false;
    }, options);
    dialog.addEventListener("keydown", event => {
        // Chromium otherwise uses the first Escape to clear a populated search field.
        if (event.key === "Escape" && !event.isComposing && event.target.matches("input[type=search]")) {
            event.preventDefault();
            close(dialog);
        }
    }, options);
    dialog.addEventListener("close", () => listeners.abort(), options);
}

export function close(dialog) {
    if (dialog?.open) dialog.close();
}
