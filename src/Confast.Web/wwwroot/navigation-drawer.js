function openDrawer(dialog, trigger) {
    if (dialog.open || !trigger.getClientRects().length) return false;

    // Safari does not always focus a button when it is tapped or clicked.
    trigger.focus({ preventScroll: true });
    dialog.showModal();
    trigger.setAttribute("aria-expanded", "true");

    const listeners = new AbortController();
    const options = { signal: listeners.signal };
    const resize = new ResizeObserver(() => {
        if (!trigger.getClientRects().length) dialog.close();
    });
    resize.observe(trigger);

    let backdropPressed = false;
    dialog.addEventListener("pointerdown", event => {
        backdropPressed = event.target === dialog;
    }, options);
    dialog.addEventListener("click", event => {
        const destination = event.target.closest("nav a[href]");
        if ((backdropPressed && event.target === dialog)
            || (destination && !event.ctrlKey && !event.metaKey && !event.shiftKey && !event.altKey)) {
            dialog.close();
        }
        backdropPressed = false;
    }, options);
    dialog.addEventListener("close", () => {
        listeners.abort();
        resize.disconnect();
        trigger.setAttribute("aria-expanded", "false");
    }, { once: true });
    return true;
}

window.confastNavigation = {
    openCategory(category) {
        const drawer = document.getElementById("navigation-drawer");
        const trigger = document.getElementById(`sidebar-${category}-toggle`);
        if (drawer && trigger && openDrawer(drawer, trigger)) {
            document.getElementById(`drawer-${category}-toggle`)?.focus();
        }
    },
    closeDrawer() {
        const drawer = document.getElementById("navigation-drawer");
        if (drawer?.open) drawer.close();
    }
};

function positionAccountPopover(popover) {
    const trigger = popover.parentElement.querySelector("[popovertarget='account-popover']");
    const bounds = trigger.getBoundingClientRect();
    popover.style.top = `${bounds.bottom + 6}px`;
    popover.style.right = `${Math.max(8, document.documentElement.clientWidth - bounds.right)}px`;
}

// Native popover dismissal and focus behavior also work on static account pages.
document.addEventListener("beforetoggle", event => {
    if (event.newState === "open" && event.target.matches?.("[data-account-popover]")) {
        positionAccountPopover(event.target);
    }
}, true);
window.addEventListener("resize", () => {
    const openMenu = document.querySelector("[data-account-popover]:popover-open");
    if (openMenu) positionAccountPopover(openMenu);
});

document.addEventListener("click", event => {
    if (!(event.target instanceof Element)) return;
    const accountDestination = event.target.closest("[data-account-destination]");
    if (accountDestination) accountDestination.closest(":popover-open")?.hidePopover();

    const trigger = event.target.closest("[data-navigation-open]");
    if (trigger) {
        const drawer = document.getElementById(trigger.getAttribute("aria-controls"));
        if (drawer) openDrawer(drawer, trigger);
        return;
    }

    // Account and inspection print pages render without an interactive Blazor circuit.
    const toggle = event.target.closest("[data-static-navigation-toggle]");
    if (toggle) {
        const group = document.getElementById(toggle.getAttribute("aria-controls"));
        if (group) {
            group.hidden = !group.hidden;
            toggle.setAttribute("aria-expanded", String(!group.hidden));
        }
    }
});
