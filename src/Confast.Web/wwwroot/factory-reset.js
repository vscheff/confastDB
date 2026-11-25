(() => {
    const form = document.querySelector("[data-factory-reset]");
    if (form) {
        const password = form.elements.namedItem("Password");
        const phrase = form.elements.namedItem("Phrase");
        const acknowledged = form.elements.namedItem("Acknowledged");
        const confirmed = form.elements.namedItem("Confirmed");
        const submit = form.querySelector('button[type="submit"]');
        const dialog = form.querySelector("dialog");
        const ready = () => password.value.length > 0 && phrase.value === "RESET DATABASE" && acknowledged.checked;
        const refresh = () => { submit.disabled = !ready(); };

        form.addEventListener("input", refresh);
        form.addEventListener("submit", (event) => {
            if (ready() && confirmed.value === "true") {
                submit.disabled = true;
                return;
            }
            event.preventDefault();
            if (ready()) dialog.showModal();
        });
        form.querySelector("[data-reset-cancel]").addEventListener("click", () => dialog.close());
        form.querySelector("[data-reset-confirm]").addEventListener("click", () => {
            dialog.close();
            if (!ready()) return;
            confirmed.value = "true";
            form.requestSubmit(submit);
        });
        window.addEventListener("pageshow", (event) => {
            confirmed.value = "false";
            if (event.persisted) password.value = "";
            refresh();
        });
        refresh();
    }

    const progress = document.querySelector("[data-maintenance-page]");
    const initial = document.querySelector('meta[name="confast-installation"]')?.content;
    if (!progress && !initial) return;

    async function poll() {
        try {
            const response = await fetch("/maintenance/status", { cache: "no-store" });
            if (response.ok) {
                const state = await response.json();
                if (progress && (state.phase || state.lastOutcome === "Aborted")) {
                    progress.querySelector("#maintenance-phase").textContent = state.message;
                    if (!state.phase) return;
                } else if (progress || initial !== state.generation) {
                    window.location.replace(state.phase ? "/maintenance" : "/login");
                    return;
                } else if (state.phase) {
                    window.location.replace("/maintenance");
                    return;
                }
            }
        } catch {
            // Keep polling through a server restart or transient disconnect.
        }
        window.setTimeout(poll, 5000);
    }
    window.setTimeout(poll, progress ? 1000 : 5000);
})();
