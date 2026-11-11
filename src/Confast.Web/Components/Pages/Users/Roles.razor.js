export function show(dialog) {
    if (!dialog.open) dialog.showModal();
}

export function close(dialog) {
    dialog.close();
}

export function revealPreview(preview) {
    const bounds = preview.getBoundingClientRect();
    if (bounds.top < 0 || bounds.bottom > window.innerHeight) {
        preview.scrollIntoView({ block: "end", inline: "nearest", behavior: "instant" });
    }
}
