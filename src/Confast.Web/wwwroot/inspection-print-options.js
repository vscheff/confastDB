document.addEventListener("pointerdown", event => {
    for (const menu of document.querySelectorAll("details.inspection-print-options[open]")) {
        if (!menu.contains(event.target)) {
            menu.removeAttribute("open");
        }
    }
});

document.addEventListener("keydown", event => {
    if (event.key !== "Escape") {
        return;
    }

    for (const menu of document.querySelectorAll("details.inspection-print-options[open]")) {
        menu.removeAttribute("open");
    }
});

window.confast ??= {};
window.confast.downloadFile = url => {
    const link = document.createElement("a");
    link.href = url;
    link.download = "";
    link.hidden = true;
    document.body.appendChild(link);
    link.click();
    link.remove();
};
window.confast.downloadBytes = (fileName, bytes) => {
    const blob = new Blob([new Uint8Array(bytes)], { type: "application/pdf" });
    const url = URL.createObjectURL(blob);
    const link = document.createElement("a");
    link.href = url;
    link.download = fileName;
    link.hidden = true;
    document.body.appendChild(link);
    link.click();
    link.remove();
    URL.revokeObjectURL(url);
};
