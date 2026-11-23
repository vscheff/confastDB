(() => {
    const documentRoot = document.documentElement;
    const contentSelector = ":is(.ql-editor, .email-html-preview)";
    let nextId = 0;
    let pixels;

    function colorPixels(background, foreground) {
        if (!pixels) {
            const canvas = document.createElement("canvas");
            canvas.width = canvas.height = 1;
            pixels = canvas.getContext("2d", { willReadFrequently: true });
        }
        pixels.fillStyle = background;
        pixels.fillRect(0, 0, 1, 1);
        if (foreground) {
            pixels.fillStyle = foreground;
            pixels.fillRect(0, 0, 1, 1);
        }
        return Array.from(pixels.getImageData(0, 0, 1, 1).data.slice(0, 3));
    }

    function quote(value) {
        return '"' + CSS.escape(value) + '"';
    }

    class EmailSurface extends HTMLElement {
        #id = String(++nextId);
        #styles = document.createElement("style");
        #observer = new MutationObserver(() => this.#updateColors());

        connectedCallback() {
            this.dataset.emailSurface = this.#id;
            this.#styles.media = "not (forced-colors: active)";
            document.head.append(this.#styles);
            this.#observer.observe(this, {
                subtree: true, childList: true, attributes: true,
                attributeFilter: ["style", "color", "data-light-reference"]
            });
            this.#observer.observe(documentRoot, { attributeFilter: ["data-theme"] });
            this.#updateColors();
        }

        disconnectedCallback() {
            this.#observer.disconnect();
            this.#styles.remove();
        }

        #updateColors() {
            const content = this.querySelector(contentSelector);
            if (!content || documentRoot.dataset.theme !== "dark" || this.hasAttribute("data-light-reference")) return;

            const background = getComputedStyle(content).backgroundColor;
            const backgroundRgb = colorPixels(background);
            const scope = 'html[data-theme="dark"] [data-email-surface="' + this.#id
                + '"]:not([data-light-reference]) ' + contentSelector + ' ';
            const rules = new Map();
            const adjusted = new Map();

            for (const element of content.querySelectorAll("[style], font[color]")) {
                const inlineStyle = element.getAttribute("style");
                const inlineColor = element.style.color;
                const fontColor = element.localName === "font" ? element.getAttribute("color") : null;
                if (!inlineColor && !fontColor) continue;

                const styleSelector = inlineStyle === null ? ":not([style])" : "[style=" + quote(inlineStyle) + "]";
                const selector = inlineColor ? styleSelector : "font[color=" + quote(fontColor) + "]" + styleSelector;
                if (rules.has(selector)) continue;

                const authored = inlineColor || (/^[0-9a-f]{6}$/i.test(fontColor) ? "#" + fontColor : fontColor);
                if (!CSS.supports("color", authored)
                    || /^(?:currentcolor|inherit|initial|unset|revert(?:-layer)?|var\()/i.test(authored)) continue;

                if (!adjusted.has(authored)) {
                    const foreground = colorPixels(background, authored);
                    adjusted.set(authored, window.confastAppearance.darkSurfaceTextColor(foreground, backgroundRgb));
                }
                const color = adjusted.get(authored);
                // A stylesheet keeps the presentation out of Quill's saved HTML.
                // Text fill also covers authored !important colors and resets inherited paint at nested colors.
                rules.set(selector, scope + selector + " { "
                    + (color ? "color: " + color + " !important; " : "")
                    + "-webkit-text-fill-color: " + (color || "currentColor") + "; }");
            }

            const css = Array.from(rules.values()).join("\n");
            if (this.#styles.textContent !== css) this.#styles.textContent = css;
        }
    }

    customElements.define("confast-email-surface", EmailSurface);
})();
