(() => {
    const search = document.querySelector("#player-search");
    const form = search?.querySelector("form");
    const button = form?.querySelector('button[type="submit"]');
    const message = search?.querySelector("#player-search-message");
    const announcement = search?.querySelector("[data-search-announcement]");
    const seconds = Number(search?.dataset.searchRetrySeconds);
    if (!form || !button || !message || !announcement || message.dataset.searchState !== "busy" ||
        !Number.isInteger(seconds) || seconds < 1 || seconds > 10 ||
        !search.dataset.searchBusy?.includes("{0}") || !search.dataset.searchReady) return;

    // A deadline catches up after throttled timers or a restored page.
    const deadline = Date.now() + seconds * 1000;
    let timer;
    let finished = false;
    message.setAttribute("role", "timer");
    message.setAttribute("aria-live", "off");

    function update() {
        const remaining = Math.max(0, Math.ceil((deadline - Date.now()) / 1000));
        button.disabled = remaining > 0;
        const text = remaining > 0
            ? search.dataset.searchBusy.replace("{0}", remaining)
            : search.dataset.searchReady;
        if (message.textContent !== text) message.textContent = text;
        if (remaining === 0) {
            clearInterval(timer);
            if (!finished) announcement.textContent = text;
            finished = true;
        }
        return remaining;
    }

    function resume() {
        clearInterval(timer);
        if (update() > 0) timer = setInterval(update, 1000);
    }

    form.addEventListener("submit", event => {
        if (update() > 0) event.preventDefault();
    });
    document.addEventListener("visibilitychange", () => {
        if (!document.hidden) update();
    });
    window.addEventListener("pagehide", () => clearInterval(timer));
    window.addEventListener("pageshow", resume);
    resume();
})();
