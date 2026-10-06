window.confastChatGifs = (() => {
    // Search lives inside the compose form; Enter must not submit the text draft.
    document.addEventListener("keydown", event => {
        if (event.key === "Enter" && event.target.matches("[data-chat-gif-search]")) event.preventDefault();
    }, true);
    async function request(key, path, parameters = {}, budget = null) {
        if (!key) return { error: "GIF search is not configured. Set Giphy:ApiKey in the server configuration.", gifs: [], total: 0 };
        const url = new URL(`https://api.giphy.com/v1/gifs${path}`);
        url.search = new URLSearchParams({ api_key: key, ...parameters });
        try {
            if (budget) await budget.invokeMethodAsync("RecordGifRequestAsync");
            const response = await fetch(url, { signal: AbortSignal.timeout(15000), credentials: "omit" });
            if (!response.ok) return { error: response.status === 429 ? "GIPHY's request limit was reached. Try again later." : "GIPHY is unavailable. Try again.", gifs: [], total: 0 };
            const payload = await response.json();
            const data = Array.isArray(payload.data) ? payload.data : payload.data ? [payload.data] : [];
            return { gifs: data.map(gif => ({
                id: gif.id, title: gif.title || "GIF",
                previewUrl: gif.images.fixed_width.url,
                url: gif.images.downsized?.url || gif.images.original.url,
                sourceUrl: gif.url,
                attribution: gif.user?.display_name || gif.user?.username || "GIPHY"
            })), total: payload.pagination?.total_count || data.length, error: null };
        } catch {
            return { error: "Could not reach GIPHY. Check your connection and try again.", gifs: [], total: 0 };
        }
    }
    // A history render may mount dozens of GIFs together. Combine lookups without
    // retaining media URLs after the requesting components receive their results.
    const pending = new Map();
    function byId(key, id, budget) {
        return new Promise(resolve => {
            if (!pending.has(key)) {
                pending.set(key, []);
                setTimeout(async () => {
                    const jobs = pending.get(key);
                    pending.delete(key);
                    const ids = [...new Set(jobs.map(job => job.id))];
                    for (let start = 0; start < ids.length; start += 50) {
                        const batch = ids.slice(start, start + 50);
                        const result = await request(key, "", { ids: batch.join(",") }, jobs[0].budget);
                        for (const job of jobs.filter(job => batch.includes(job.id)))
                            job.resolve({ ...result, gifs: result.gifs.filter(gif => gif.id === job.id) });
                    }
                }, 25);
            }
            pending.get(key).push({ id, resolve, budget });
        });
    }
    return {
        search: (key, query, offset = 0, limit = 24) => request(key, query ? "/search" : "/trending", { q: query, offset, limit, rating: "pg-13", lang: "en" }),
        byIds: (key, ids, budget) => ids.length === 1 ? byId(key, ids[0], budget) : ids.length ? request(key, "", { ids: ids.join(",") }, budget) : Promise.resolve({ gifs: [], total: 0, error: null })
    };
})();
