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

    const droppedFile = event.dataTransfer?.files?.[0];
    const fileInput = dropZone.querySelector('input[type="file"]');
    if (!droppedFile || !fileInput || fileInput.disabled) {
        return;
    }

    const selectedFiles = new DataTransfer();
    selectedFiles.items.add(droppedFile);
    fileInput.files = selectedFiles.files;
    fileInput.dispatchEvent(new Event("change", { bubbles: true }));
}, true);
