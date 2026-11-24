export function timeZone() {
    try {
        return Intl.DateTimeFormat().resolvedOptions().timeZone ?? null;
    } catch {
        return null;
    }
}

export function open(dialog) {
    if (dialog?.isConnected && !dialog.open) dialog.showModal();
}

export function close(dialog) {
    if (dialog?.open) dialog.close();
}
