import * as pdfjsLib from "../lib/pdfjs/5.7.284/build/pdf.mjs";

pdfjsLib.GlobalWorkerOptions.workerSrc =
    "../lib/pdfjs/5.7.284/build/pdf.worker.mjs";

const viewerSurface = document.getElementById("viewer-surface");
const canvas = document.getElementById("pdf-page");
const message = document.getElementById("viewer-message");
const previousButton = document.getElementById("previous-page");
const nextButton = document.getElementById("next-page");
const zoomOutButton = document.getElementById("zoom-out");
const zoomInButton = document.getElementById("zoom-in");
const fitWidthButton = document.getElementById("fit-width");
const pageStatus = document.getElementById("page-status");
const zoomStatus = document.getElementById("zoom-status");
const downloadLink = document.getElementById("download-pdf");

let documentProxy;
let currentPageNumber = 1;
let zoomMultiplier = 1;
let fitWidth = true;
let renderGeneration = 0;

function getDocumentUrl(parameterName = "file") {
    const file = new URLSearchParams(window.location.search).get(parameterName);
    if (!file) {
        if (parameterName === "original") {
            return null;
        }

        throw new Error("No PDF document was specified.");
    }

    const url = new URL(file, window.location.origin);
    const allowedPaths = [
        /^\/parts\/\d+\/inspection-criteria\/\d+\/master-print$/,
        /^\/inspections\/\d+\/certifications\/documents\/\d+(?:\/preview)?$/
    ];
    if (url.origin !== window.location.origin
        || !allowedPaths.some(pattern => pattern.test(url.pathname))) {
        throw new Error("The PDF document URL is invalid.");
    }

    return url;
}

function updateControls() {
    const pageCount = documentProxy?.numPages ?? 1;
    previousButton.disabled = currentPageNumber <= 1;
    nextButton.disabled = currentPageNumber >= pageCount;
    pageStatus.textContent = `Page ${currentPageNumber} of ${pageCount}`;
    zoomStatus.textContent = fitWidth
        ? "Fit width"
        : `${Math.round(zoomMultiplier * 100)}%`;
}

async function renderCurrentPage() {
    if (!documentProxy) {
        return;
    }

    const generation = ++renderGeneration;
    const page = await documentProxy.getPage(currentPageNumber);
    if (generation !== renderGeneration) {
        return;
    }

    const baseViewport = page.getViewport({ scale: 1 });
    const availableWidth = Math.max(100, viewerSurface.clientWidth - 48);
    const fitScale = availableWidth / baseViewport.width;
    const scale = fitWidth ? fitScale : fitScale * zoomMultiplier;
    const viewport = page.getViewport({ scale });
    const outputScale = window.devicePixelRatio || 1;
    const context = canvas.getContext("2d", { alpha: false });

    canvas.width = Math.floor(viewport.width * outputScale);
    canvas.height = Math.floor(viewport.height * outputScale);
    canvas.style.width = `${Math.floor(viewport.width)}px`;
    canvas.style.height = `${Math.floor(viewport.height)}px`;
    canvas.setAttribute("aria-label", `PDF page ${currentPageNumber} of ${documentProxy.numPages}`);

    await page.render({
        canvasContext: context,
        viewport,
        transform: outputScale === 1
            ? null
            : [outputScale, 0, 0, outputScale, 0, 0],
        annotationMode: pdfjsLib.AnnotationMode.DISABLE,
        intent: "display"
    }).promise;

    if (generation !== renderGeneration) {
        return;
    }

    document.body.classList.add("viewer-ready");
    message.hidden = true;
    updateControls();
}

function showError(error) {
    console.error(error);
    message.textContent = "The PDF could not be displayed. Download the original file to view it locally.";
    message.classList.add("viewer-message-error");
    message.hidden = false;
}

previousButton.addEventListener("click", async () => {
    if (currentPageNumber <= 1) {
        return;
    }

    currentPageNumber--;
    await renderCurrentPage();
});

nextButton.addEventListener("click", async () => {
    if (!documentProxy || currentPageNumber >= documentProxy.numPages) {
        return;
    }

    currentPageNumber++;
    await renderCurrentPage();
});

zoomOutButton.addEventListener("click", async () => {
    fitWidth = false;
    zoomMultiplier = Math.max(.5, zoomMultiplier - .25);
    await renderCurrentPage();
});

zoomInButton.addEventListener("click", async () => {
    fitWidth = false;
    zoomMultiplier = Math.min(3, zoomMultiplier + .25);
    await renderCurrentPage();
});

fitWidthButton.addEventListener("click", async () => {
    fitWidth = true;
    zoomMultiplier = 1;
    await renderCurrentPage();
});

let resizeTimer;
window.addEventListener("resize", () => {
    if (!fitWidth) {
        return;
    }

    window.clearTimeout(resizeTimer);
    resizeTimer = window.setTimeout(() => renderCurrentPage().catch(showError), 150);
});

try {
    const documentUrl = getDocumentUrl();
    downloadLink.href = getDocumentUrl("original")?.href ?? documentUrl.href;
    const loadingTask = pdfjsLib.getDocument({
        url: documentUrl.href,
        cMapUrl: "../lib/pdfjs/5.7.284/cmaps/",
        cMapPacked: true,
        standardFontDataUrl: "../lib/pdfjs/5.7.284/standard_fonts/",
        wasmUrl: "../lib/pdfjs/5.7.284/wasm/",
        enableXfa: false,
        isEvalSupported: false
    });
    documentProxy = await loadingTask.promise;
    updateControls();
    await renderCurrentPage();
} catch (error) {
    showError(error);
}
