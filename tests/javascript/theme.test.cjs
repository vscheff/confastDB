const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
const script = fs.readFileSync(path.join(__dirname, "../../src/Confast.Web/wwwroot/theme.js"), "utf8");

function loadTheme(mode, accent, prefersDark = false) {
    const colors = new Map();
    const attributes = new Set();
    const root = {
        dataset: { appearanceMode: mode, accentColor: accent },
        style: { setProperty: (name, value) => colors.set(name, value), removeProperty: name => colors.delete(name) },
        hasAttribute: name => attributes.has(name),
        toggleAttribute: (name, enabled) => enabled ? attributes.add(name) : attributes.delete(name)
    };
    const media = { matches: prefersDark, addEventListener(_, handler) { this.change = handler; } };
    vm.runInNewContext(script, {
        document: { documentElement: root, querySelector: () => null, addEventListener() {} },
        window: { matchMedia: () => media },
        MutationObserver: class { observe() {} }
    });
    return { root, colors, media };
}

function luminance(hex) {
    const [r, g, b] = hex.match(/[0-9a-f]{2}/gi).map(digits => {
        const s = Number.parseInt(digits, 16) / 255;
        return s <= 0.04045 ? s / 12.92 : ((s + 0.055) / 1.055) ** 2.4;
    });
    return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}

function ratio(first, second) {
    const values = [luminance(first), luminance(second)].sort((a, b) => b - a);
    return (values[0] + 0.05) / (values[1] + 0.05);
}

test("system mode resolves before the UI loads and follows media changes", () => {
    const { root, media, colors } = loadTheme("system", "#FFEE00", true);
    assert.equal(root.dataset.theme, "dark");
    const darkFill = colors.get("--accent");
    media.matches = false;
    media.change();
    assert.equal(root.dataset.theme, "light");
    assert.equal(root.dataset.appearanceMode, "system");
    assert.equal(root.dataset.accentColor, "#FFEE00");
    assert.notEqual(colors.get("--accent"), darkFill);
});

test("explicit mode ignores the device and the default accent uses the CSS palette", () => {
    const { root, media, colors } = loadTheme("dark", "#176B9C");
    assert.equal(root.dataset.theme, "dark");
    media.matches = true;
    media.change();
    assert.equal(root.dataset.theme, "dark");
    assert.equal(colors.size, 0);
    assert.equal(loadTheme("light", "red", true).root.dataset.theme, "light");
    assert.equal(loadTheme("light", "red").colors.size, 0);
});

test("presets and extreme custom colors keep labels, links and focus indicators readable", () => {
    const samples = [
        "#000000", "#FFFFFF", "#FFFF00", "#000033", "#FFD6EC", "#111111", "#888888", "#800000",
        "#0F766E", "#7C3AED", "#C026D3", "#DB2777", "#C2410C", "#64748B"
    ];
    for (const mode of ["light", "dark"]) {
        const controlSurfaces = mode === "dark" ? ["#151e28", "#2a3746", "#3c4b5b"] : ["#fffFFF", "#f4f6f8", "#e5ebf1"];
        const textSurfaces = mode === "dark" ? ["#4d5b6a", "#45292e"] : ["#cccccc", "#dcf5e8"];
        for (const sample of samples) {
            const { colors } = loadTheme(mode, sample);
            const context = `${sample} in ${mode}`;
            const accent = colors.get("--accent");
            const foreground = colors.get("--on-accent");
            const hover = colors.get("--accent-hover");
            const active = colors.get("--accent-active");
            assert.notEqual(accent, hover, context);
            assert.notEqual(hover, active, context);
            for (const fill of [accent, hover, active]) {
                assert.ok(ratio(fill, foreground) >= 4.5, context);
                for (const surface of controlSurfaces) assert.ok(ratio(fill, surface) >= 3, context);
            }
            for (const surface of textSurfaces) {
                assert.ok(ratio(colors.get("--accent-text"), surface) >= 4.5, context);
                assert.ok(ratio(colors.get("--accent-border"), surface) >= 3, context);
            }
            assert.ok(ratio(colors.get("--header"), "#ffffff") >= 7, context);
        }
    }
});
