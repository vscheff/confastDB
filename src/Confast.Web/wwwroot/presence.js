window.confastPresence = (() => {
    let lastActivity = Date.now();
    let watching = false;
    const activity = () => { lastActivity = Date.now(); };
    const events = ["pointerdown", "keydown", "touchstart", "scroll", "focus"];

    return {
        start() {
            if (watching) return;
            watching = true;
            activity();
            for (const event of events) document.addEventListener(event, activity, { passive: true, capture: true });
        },
        sample() {
            return {
                idleSeconds: Math.max(0, (Date.now() - lastActivity) / 1000)
            };
        },
        stop() {
            if (!watching) return;
            watching = false;
            for (const event of events) document.removeEventListener(event, activity, true);
        }
    };
})();
