(() => {
    const root = document.documentElement;
    const system = window.matchMedia("(prefers-color-scheme: dark)");
    const defaultAccent = "#176B9C";
    const black = [0, 0, 0];
    const white = [255, 255, 255];
    const rendered = new WeakMap();
    const accentProperties = [
        "--accent", "--on-accent", "--accent-hover", "--accent-active",
        "--accent-text", "--accent-text-hover", "--accent-border", "--header"
    ];

    function rgb(hex) {
        return [1, 3, 5].map(index => Number.parseInt(hex.slice(index, index + 2), 16));
    }

    function hex(color) {
        return "#" + color.map(value => value.toString(16).padStart(2, "0")).join("");
    }

    function mix(from, to, amount) {
        return from.map((value, index) => Math.round(value + (to[index] - value) * amount));
    }

    function luminance(color) {
        const [r, g, b] = color.map(value => {
            const channel = value / 255;
            return channel <= 0.04045 ? channel / 12.92 : ((channel + 0.055) / 1.055) ** 2.4;
        });
        return 0.2126 * r + 0.7152 * g + 0.0722 * b;
    }

    function contrast(first, second) {
        const a = luminance(first) + 0.05;
        const b = luminance(second) + 0.05;
        return Math.max(a, b) / Math.min(a, b);
    }

    function withContrast(color, background, minimum, toward) {
        const lighter = toward === white;
        const meetsContrast = candidate => (lighter
            ? luminance(candidate) >= luminance(background)
            : luminance(candidate) <= luminance(background))
            && contrast(candidate, background) >= minimum;
        if (meetsContrast(color)) return color;

        // Each channel moves monotonically toward black or white. Check the
        // rounded sRGB result so the actual CSS color still meets the threshold.
        let low = 0;
        let high = 1;
        for (let step = 0; step < 8; step++) {
            const midpoint = (low + high) / 2;
            if (meetsContrast(mix(color, toward, midpoint))) high = midpoint;
            else low = midpoint;
        }
        return mix(color, toward, high);
    }

    function palette(accent, theme) {
        const dark = theme === "dark";
        const color = rgb(accent);
        const toward = dark ? white : black;
        const opposite = dark ? black : white;
        // Bound the luminance of app surfaces, including tinted selection and
        // hover surfaces, to give controls and links enough contrast in context.
        const controlSurface = rgb(dark ? "#3C4B5B" : "#E5EBF1");
        const textSurface = rgb(dark ? "#4D5B6A" : "#CCCCCC");
        const fill = withContrast(color, controlSurface, 3, toward);
        const onFill = contrast(fill, white) >= contrast(fill, black) ? white : black;
        const text = withContrast(color, textSurface, 4.5, toward);
        const border = withContrast(color, textSurface, 3, toward);
        const headerTint = mix(rgb(dark ? "#101924" : "#0F2335"), color, dark ? 0.45 : 0.37);
        const header = withContrast(headerTint, white, 7, black);

        const readable = candidate => contrast(candidate, onFill) >= 4.5 && contrast(candidate, controlSurface) >= 3;
        let variants;
        for (const strength of [0.20, 0.10, 0.06]) {
            const directions = luminance(fill) < 0.035 || luminance(fill) > 0.83
                ? [opposite, toward] : [toward, opposite];
            for (const end of directions) {
                const hover = mix(fill, end, strength / 2);
                const active = mix(fill, end, strength);
                if (readable(hover) && readable(active) && hex(hover) !== hex(fill) && hex(active) !== hex(hover)) {
                    variants = [hover, active];
                    break;
                }
            }
            if (variants) break;
        }

        // If a state cannot keep the same label and enough contrast, keep the base fill.
        const [hover, active] = variants ?? [fill, fill];
        return [fill, onFill, hover, active, text, mix(text, toward, 0.14), border, header];
    }

    function render(element, theme, accent) {
        accent = /^#[0-9a-f]{6}$/i.test(accent) ? accent.toUpperCase() : defaultAccent;
        const custom = accent !== defaultAccent;
        const key = theme + accent;
        if (rendered.get(element) !== key || element.dataset.theme !== theme) {
            const variants = custom ? palette(accent, theme) : null;
            accentProperties.forEach((name, index) => {
                if (variants) element.style.setProperty(name, hex(variants[index]));
                else element.style.removeProperty(name);
            });
            element.toggleAttribute("data-custom-accent", custom);
            if (element.dataset.theme !== theme) element.dataset.theme = theme;
            rendered.set(element, key);
        }
    }

    function renderDocument() {
        const mode = root.dataset.appearanceMode;
        const theme = mode === "dark" || (mode !== "light" && system.matches) ? "dark" : "light";
        render(root, theme, root.dataset.accentColor);
        const preview = document.querySelector("[data-theme-preview][data-preview-accent]");
        if (preview) render(preview, theme, preview.dataset.previewAccent);
    }

    if (root.hasAttribute("data-embedded-appearance")) {
        // The same-origin PDF viewer follows its parent's chrome without
        // reloading the document or changing anything drawn on the canvas.
        let parentRoot;
        try { if (window.parent !== window) parentRoot = window.parent.document.documentElement; }
        catch { /* A standalone viewer can also be embedded by another origin. */ }
        if (parentRoot?.dataset.theme) {
            const sync = () => render(root, parentRoot.dataset.theme, parentRoot.dataset.accentColor);
            const observer = new MutationObserver(sync);
            const start = () => {
                sync();
                observer.observe(parentRoot, { attributeFilter: ["data-theme", "data-accent-color"] });
            };
            start();
            window.addEventListener("pagehide", () => observer.disconnect());
            window.addEventListener("pageshow", start);
            return;
        }
    }

    renderDocument();
    system.addEventListener("change", renderDocument);
    // Enhanced navigation can replace document attributes with a new HTTP snapshot.
    new MutationObserver(renderDocument).observe(root, {
        attributeFilter: ["data-appearance-mode", "data-accent-color", "data-theme"]
    });

    document.addEventListener("input", event => {
        const form = event.target.closest?.("[data-appearance-custom]");
        if (!form) return;
        const { picker, hex: field } = form.elements;
        const preview = form.closest("[data-appearance-editor]").querySelector("[data-theme-preview]");
        if (event.target === picker) field.value = picker.value.toUpperCase();
        let accent = field.value.trim().replace(/^#/, "");
        if (/^[0-9a-f]{3}$/i.test(accent)) accent = [...accent].map(digit => digit.repeat(2)).join("");
        const valid = /^[0-9a-f]{6}$/i.test(accent);
        field.setCustomValidity(valid || !field.value ? "" : "Enter a valid three- or six-digit HEX color.");
        if (!valid) return;
        preview.dataset.previewAccent = "#" + accent.toUpperCase();
        picker.value = preview.dataset.previewAccent;
        render(preview, root.dataset.theme, preview.dataset.previewAccent);
    });

    window.confastAppearance = {
        resolvedTheme: () => root.dataset.theme,
        watchAccount(element) {
            function sync() {
                if (!element.isConnected) return;
                const mode = element.dataset.savedMode;
                const accent = element.dataset.savedAccent;
                if (!mode) return;
                root.dataset.appearanceMode = mode;
                root.dataset.accentColor = accent;
                renderDocument();
            }
            // Only observe this component's committed values. A resumed circuit
            // can deliver its pending render without additional JS interop.
            const observer = new MutationObserver(sync);
            observer.observe(element, { attributeFilter: ["data-saved-mode", "data-saved-accent"] });
            sync();
        },
        readCustomAccent: form => form.elements.hex.value,
        resetEditor(editor, accent) {
            const { picker, hex: field } = editor.querySelector("[data-appearance-custom]").elements;
            const preview = editor.querySelector("[data-theme-preview]");
            field.setCustomValidity("");
            field.value = picker.value = accent;
            delete preview.dataset.previewAccent;
            delete preview.dataset.theme;
            preview.removeAttribute("data-custom-accent");
            accentProperties.forEach(name => preview.style.removeProperty(name));
            rendered.delete(preview);
        }
    };
})();
