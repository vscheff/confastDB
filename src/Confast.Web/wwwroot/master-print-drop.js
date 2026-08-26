const dropZoneSelector = ".master-print-drop-zone";

document.addEventListener("dragover", event => {
    const dropZone = event.target.closest?.(dropZoneSelector);
    if (!dropZone) {
        return;
    }

    event.preventDefault();
    event.stopPropagation();
    event.dataTransfer.dropEffect = "copy";
    dropZone.classList.add("master-print-drop-active");
}, true);

document.addEventListener("dragleave", event => {
    const dropZone = event.target.closest?.(dropZoneSelector);
    if (!dropZone || dropZone.contains(event.relatedTarget)) {
        return;
    }

    dropZone.classList.remove("master-print-drop-active");
}, true);

document.addEventListener("drop", event => {
    const dropZone = event.target.closest?.(dropZoneSelector);
    if (!dropZone) {
        return;
    }

    event.preventDefault();
    event.stopPropagation();
    dropZone.classList.remove("master-print-drop-active");

    const droppedFiles = event.dataTransfer?.files;
    const fileInputId = dropZone.getAttribute("for");
    const fileInput = fileInputId ? document.getElementById(fileInputId) : null;
    if (!droppedFiles?.length || !fileInput || fileInput.disabled) {
        return;
    }

    const selectedFiles = new DataTransfer();
    const filesToAdd = fileInput.multiple ? droppedFiles : [droppedFiles[0]];
    for (const file of filesToAdd) {
        selectedFiles.items.add(file);
    }
    fileInput.files = selectedFiles.files;
    fileInput.dispatchEvent(new Event("change", { bubbles: true }));
}, true);
