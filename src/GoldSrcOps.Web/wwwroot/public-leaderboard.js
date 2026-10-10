(() => {
    const page = document.querySelector(".leaderboard-page");
    const button = page?.querySelector("[data-leaderboard-refresh]");
    const status = page?.querySelector("#leaderboard-refresh-status");
    let panel = page?.querySelector("[data-leaderboard-panel]");
    if (!button || !status || !panel) return;

    const refreshInterval = 60_000;
    const requestTimeout = 8_000;
    const maximumBytes = 131_072;
    let observedAt = performance.now();
    let initialAge = Number(panel.dataset.ageSeconds);
    let lastRequest = -Infinity;
    let nextRefresh = observedAt + refreshInterval;
    let controller = null;
    let stopped = false;
    let tickId;

    function changeState(state) {
        panel.dataset.state = state;
        const label = panel.querySelector(".leaderboard-state");
        label.className = `leaderboard-state leaderboard-state--${state}`;
        label.textContent = state === "stale" ? "Устаревший снимок" : "Рейтинг временно недоступен";
        panel.querySelector("[data-leaderboard-notice]").hidden = state !== "stale";
    }

    function updateAge() {
        const age = initialAge + Math.max(0, performance.now() - observedAt) / 1000;
        const display = panel.querySelector("[data-leaderboard-age]");
        if (display) display.textContent = `Возраст: ${Math.ceil(age)} сек.`;
        if (panel.dataset.state === "fresh" && age > 180) changeState("stale");
        if (age > 86_400 && panel.dataset.state !== "unavailable") {
            changeState("unavailable");
            panel.querySelector(".leaderboard-table")?.remove();
            panel.querySelector(".leaderboard-updated")?.remove();
            panel.querySelector(".leaderboard-empty")?.remove();
            const notice = document.createElement("p");
            notice.className = "leaderboard-empty";
            notice.textContent = "Не удалось получить рейтинг. Попробуй обновить позже.";
            panel.append(notice);
        }
    }

    async function readPage(response) {
        if (!response.ok || !response.headers.get("content-type")?.includes("text/html")) throw new Error();
        const reader = response.body.getReader();
        const decoder = new TextDecoder("utf-8", { fatal: true });
        let bytes = 0;
        let html = "";
        try {
            while (true) {
                const { done, value } = await reader.read();
                if (done) break;
                bytes += value.byteLength;
                if (bytes > maximumBytes) throw new Error();
                html += decoder.decode(value, { stream: true });
            }
            return html + decoder.decode();
        } finally {
            await reader.cancel().catch(() => {});
            reader.releaseLock();
        }
    }

    async function refresh() {
        if (stopped || document.hidden || controller || performance.now() - lastRequest < 5_000) return;
        lastRequest = performance.now();
        nextRefresh = lastRequest + refreshInterval;
        const active = new AbortController();
        controller = active;
        const timeout = setTimeout(() => active.abort(), requestTimeout);
        button.setAttribute("aria-disabled", "true");
        panel.setAttribute("aria-busy", "true");
        status.textContent = "Обновляем рейтинг...";
        try {
            const response = await fetch("/leaderboard", {
                method: "GET", credentials: "omit", cache: "no-store", redirect: "error", signal: active.signal
            });
            const html = await readPage(response);
            if (stopped || document.hidden || active.signal.aborted) return;
            const next = new DOMParser().parseFromString(html, "text/html").querySelector("[data-leaderboard-panel]");
            const age = Number(next?.dataset.ageSeconds);
            if (!next || !Number.isFinite(age) || age < 0 ||
                !["fresh", "stale", "unavailable"].includes(next.dataset.state) ||
                !next.querySelector(".leaderboard-state") || !next.querySelector("[data-leaderboard-notice]") ||
                next.querySelectorAll(".leaderboard-table tbody tr").length > 10 ||
                next.querySelector("script, iframe, object, embed")) throw new Error();
            panel.replaceWith(document.importNode(next, true));
            panel = page.querySelector("[data-leaderboard-panel]");
            observedAt = performance.now();
            initialAge = age;
            updateAge();
            status.textContent = panel.dataset.state === "fresh"
                ? "Проверка завершена. Свежий снимок получен."
                : "Проверка завершена. Свежий снимок пока недоступен.";
        } catch {
            if (!stopped && !document.hidden && active.signal.reason !== "hidden") {
                if (panel.dataset.state === "fresh") changeState("stale");
                status.textContent = "Не удалось обновить рейтинг. Повторим через минуту; можно обновить вручную.";
                updateAge();
            }
        } finally {
            clearTimeout(timeout);
            if (controller === active) controller = null;
            button.removeAttribute("aria-disabled");
            panel.removeAttribute("aria-busy");
        }
    }

    function tick() {
        if (stopped || document.hidden) return;
        updateAge();
        if (performance.now() >= nextRefresh) void refresh();
    }

    button.addEventListener("click", event => { event.preventDefault(); void refresh(); });
    document.addEventListener("visibilitychange", () => {
        if (document.hidden) {
            controller?.abort("hidden");
            status.textContent = "Автообновление приостановлено, пока вкладка скрыта.";
        } else {
            status.textContent = "Автообновление раз в минуту, пока вкладка видима.";
            tick();
        }
    });
    window.addEventListener("pagehide", () => { stopped = true; controller?.abort(); clearInterval(tickId); });
    window.addEventListener("pageshow", () => {
        if (!stopped) return;
        stopped = false;
        tickId = setInterval(tick, 1000);
        tick();
    });
    status.textContent = "Автообновление раз в минуту, пока вкладка видима.";
    updateAge();
    tickId = setInterval(tick, 1000);
})();
