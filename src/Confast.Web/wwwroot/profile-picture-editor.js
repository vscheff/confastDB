window.confastProfilePictureEditor = {
    state: null,

    async prepare(inputId, maxSourceBytes) {
        this.dispose();
        const input = document.getElementById(inputId);
        const file = input?.files?.[0];
        if (!(input instanceof HTMLInputElement) || !file || file.size === 0 || file.size > maxSourceBytes)
            throw new Error("The selected picture is missing or too large.");

        const url = URL.createObjectURL(file);
        try {
            const image = new Image();
            image.src = url;
            await image.decode();
            if (!image.naturalWidth || !image.naturalHeight || image.naturalWidth * image.naturalHeight > 60_000_000)
                throw new Error("The picture dimensions are too large to edit.");
            this.state = { input, url, image, zoom: 1, panX: 0, panY: 0, canvas: null, slider: null, handlers: null, drag: null };
        } catch (error) {
            URL.revokeObjectURL(url);
            input.value = "";
            throw error;
        }
    },

    mount() {
        const state = this.state;
        const canvas = document.getElementById("chat-picture-edit-canvas");
        const slider = document.getElementById("chat-picture-zoom");
        if (!state || !(canvas instanceof HTMLCanvasElement) || !(slider instanceof HTMLInputElement))
            throw new Error("The picture editor is unavailable.");

        state.canvas = canvas;
        state.slider = slider;
        const pointerDown = event => {
            if (event.pointerType === "mouse" && event.button !== 0) return;
            state.drag = { id: event.pointerId, x: event.clientX, y: event.clientY, panX: state.panX, panY: state.panY };
            canvas.setPointerCapture(event.pointerId);
        };
        const pointerMove = event => {
            if (!state.drag || event.pointerId !== state.drag.id) return;
            const bounds = canvas.getBoundingClientRect();
            const pixelsPerCssPixel = canvas.width / bounds.width;
            state.panX = state.drag.panX + (event.clientX - state.drag.x) * pixelsPerCssPixel;
            state.panY = state.drag.panY + (event.clientY - state.drag.y) * pixelsPerCssPixel;
            this.draw();
        };
        const pointerUp = event => {
            if (state.drag?.id !== event.pointerId) return;
            state.drag = null;
            if (canvas.hasPointerCapture(event.pointerId)) canvas.releasePointerCapture(event.pointerId);
        };
        const zoomInput = () => {
            state.zoom = Number(slider.value);
            this.draw();
        };
        const keyDown = event => {
            const step = event.shiftKey ? 30 : 10;
            if (event.key === "ArrowLeft") state.panX -= step;
            else if (event.key === "ArrowRight") state.panX += step;
            else if (event.key === "ArrowUp") state.panY -= step;
            else if (event.key === "ArrowDown") state.panY += step;
            else return;
            event.preventDefault();
            this.draw();
        };
        canvas.addEventListener("pointerdown", pointerDown);
        canvas.addEventListener("pointermove", pointerMove);
        canvas.addEventListener("pointerup", pointerUp);
        canvas.addEventListener("pointercancel", pointerUp);
        canvas.addEventListener("keydown", keyDown);
        slider.addEventListener("input", zoomInput);
        state.handlers = { pointerDown, pointerMove, pointerUp, keyDown, zoomInput };
        this.draw();
        document.querySelector(".chat-picture-edit-close")?.focus();
    },

    draw() {
        const state = this.state;
        if (!state?.canvas) return;
        const { canvas, image } = state;
        const context = canvas.getContext("2d");
        if (!context) throw new Error("The picture editor cannot draw this image.");
        const size = canvas.width;
        const diameter = size * 0.78;
        const scale = Math.max(diameter / image.naturalWidth, diameter / image.naturalHeight) * state.zoom;
        const width = image.naturalWidth * scale;
        const height = image.naturalHeight * scale;
        state.panX = Math.max((diameter - width) / 2, Math.min((width - diameter) / 2, state.panX));
        state.panY = Math.max((diameter - height) / 2, Math.min((height - diameter) / 2, state.panY));

        context.clearRect(0, 0, size, size);
        context.fillStyle = "#2d2e32";
        context.fillRect(0, 0, size, size);
        context.drawImage(image, (size - width) / 2 + state.panX, (size - height) / 2 + state.panY, width, height);
        context.beginPath();
        context.rect(0, 0, size, size);
        context.arc(size / 2, size / 2, diameter / 2, 0, Math.PI * 2);
        context.fillStyle = "rgba(0, 0, 0, .68)";
        context.fill("evenodd");
        context.beginPath();
        context.arc(size / 2, size / 2, diameter / 2, 0, Math.PI * 2);
        context.strokeStyle = "#fff";
        context.lineWidth = 5;
        context.stroke();
    },

    reset() {
        const state = this.state;
        if (!state) return;
        state.zoom = 1;
        state.panX = 0;
        state.panY = 0;
        if (state.slider) state.slider.value = "1";
        this.draw();
    },

    async render(maxBytes) {
        const state = this.state;
        if (!state?.canvas) throw new Error("The picture editor is unavailable.");
        const { image, canvas } = state;
        const diameter = canvas.width * 0.78;
        const scale = Math.max(diameter / image.naturalWidth, diameter / image.naturalHeight) * state.zoom;
        const sourceSize = diameter / scale;
        const sourceX = Math.max(0, Math.min(image.naturalWidth - sourceSize,
            (image.naturalWidth - sourceSize) / 2 - state.panX / scale));
        const sourceY = Math.max(0, Math.min(image.naturalHeight - sourceSize,
            (image.naturalHeight - sourceSize) / 2 - state.panY / scale));

        for (const size of [512, 384, 320]) {
            const output = document.createElement("canvas");
            output.width = output.height = size;
            const context = output.getContext("2d");
            if (!context) throw new Error("The picture cannot be exported.");
            context.fillStyle = "#fff";
            context.fillRect(0, 0, size, size);
            context.imageSmoothingQuality = "high";
            context.drawImage(image, sourceX, sourceY, sourceSize, sourceSize, 0, 0, size, size);
            for (const quality of [0.92, 0.82, 0.7, 0.55]) {
                const blob = await new Promise(resolve => output.toBlob(resolve, "image/jpeg", quality));
                if (blob && blob.size > 0 && blob.size <= maxBytes)
                    return blob;
            }
        }
        throw new Error("The edited picture could not be compressed below 1 MB.");
    },

    dispose() {
        const state = this.state;
        if (!state) return;
        if (state.handlers) {
            const { canvas, slider, handlers } = state;
            canvas.removeEventListener("pointerdown", handlers.pointerDown);
            canvas.removeEventListener("pointermove", handlers.pointerMove);
            canvas.removeEventListener("pointerup", handlers.pointerUp);
            canvas.removeEventListener("pointercancel", handlers.pointerUp);
            canvas.removeEventListener("keydown", handlers.keyDown);
            slider.removeEventListener("input", handlers.zoomInput);
        }
        URL.revokeObjectURL(state.url);
        state.input.value = "";
        this.state = null;
    }
};
