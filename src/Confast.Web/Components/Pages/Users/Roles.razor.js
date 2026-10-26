export function show(dialog) {
    if (!dialog.open) dialog.showModal();
}

export function close(dialog) {
    dialog.close();
}
