(() => {
    const page = document.querySelector(".play-page");
    const card = page?.querySelector(".join-panel");
    const button = card?.querySelector("[data-play-refresh]");
    const status = card?.querySelector("#play-refresh-status");
    let panel = card?.querySelector("[data-play-status]");
    if (!button || !status || !panel) return;

    const refreshInterval = 60_000;
    const requestTimeout = 8_000;
    const maximumBytes = 131_072;
    let observedAt = performance.now();
    let initialAge = panel.dataset.ageSeconds === "" ? null : Number(panel.dataset.ageSeconds);
    let lastRequest = -Infinity;
    let nextRefresh = observedAt + refreshInterval;
    let controller = null;
    let stopped = false;
    let tickId;

    function markUnconfirmed(message) {
        if (panel.dataset.state === "unavailable") return;
        panel.dataset.state = "unknown";
        card.classList.remove("join-panel--online", "join-panel--offline");
        card.classList.add("join-panel--unknown");
        const label = panel.querySelector(".join-panel__state");
        label.replaceChildren();
        const signal = document.createElement("span");
        signal.className = "join-panel__signal";
        signal.setAttribute("aria-hidden", "true");
        label.append(signal, "Свежий статус не подтвержден");
        panel.querySelector(".join-panel__detail").textContent = "Свежих данных пока нет. Подключение можно попробовать, но доступность сервера не подтверждена.";
        panel.querySelectorAll(".join-panel__facts dd").forEach(fact => { fact.textContent = "Нет свежих данных"; });
        const warning = panel.querySelector("[data-play-warning]");
        warning.hidden = false;
        warning.textContent = message;
        const steam = card.querySelector(".join-panel__steam");
        if (steam) steam.textContent = "Попробовать подключиться";
    }

    function updateAge() {
        if (initialAge === null) return;
        const age = initialAge + Math.max(0, performance.now() - observedAt) / 1000;
        const display = panel.querySelector("[data-play-age]");
        if (display) display.textContent = `Возраст: ${Math.ceil(age)} сек.`;
        if (age > 180 && ["online", "offline"].includes(panel.dataset.state)) {
            markUnconfirmed("Проверка устарела. Обнови статус перед подключением.");
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

    function validate(nextCard) {
        const next = nextCard?.querySelector("[data-play-status]");
        const connection = nextCard?.querySelector("[data-play-connection]");
        const age = next?.dataset.ageSeconds === "" ? null : Number(next?.dataset.ageSeconds);
        if (!next || !nextCard.querySelector("#join-title") ||
            !["online", "offline", "unknown", "unavailable"].includes(next.dataset.state) ||
            (age !== null && (!Number.isFinite(age) || age < 0)) ||
            (["online", "offline"].includes(next.dataset.state) && age === null) ||
            nextCard.querySelector("script, iframe, object, embed, form, img, link, meta") ||
            [...nextCard.querySelectorAll("*")].some(element => [...element.attributes].some(attribute => attribute.name.startsWith("on")))) throw new Error();
        if (next.dataset.state === "unavailable") {
            if (connection || !next.querySelector(".join-panel__notice")) throw new Error();
        } else {
            const command = connection?.querySelector(".join-panel__command")?.textContent;
            if (!next.querySelector(".join-panel__state") || !next.querySelector(".join-panel__detail") ||
                !next.querySelector("[data-play-warning]") || next.querySelectorAll(".join-panel__facts dd").length !== 2 ||
                !command?.startsWith("connect ") || command.length > 300 ||
                connection.querySelector("[data-copy-connect]")?.dataset.copyConnect !== command ||
                connection.querySelector(".join-panel__steam")?.getAttribute("href") !== `steam://${command.replace("connect ", "connect/")}`) throw new Error();
        }
        return { next, connection, age };
    }

    function updateConnection(next) {
        const current = card.querySelector("[data-play-connection]");
        if (current && next) {
            const steam = current.querySelector(".join-panel__steam");
            const newSteam = next.querySelector(".join-panel__steam");
            steam.setAttribute("href", newSteam.getAttribute("href"));
            steam.textContent = newSteam.textContent;
            const command = next.querySelector(".join-panel__command").textContent;
            current.querySelector(".join-panel__command").textContent = command;
            const copy = current.querySelector("[data-copy-connect]");
            if (copy.dataset.copyConnect !== command) current.querySelector("#play-copy-status").textContent = "";
            copy.dataset.copyConnect = command;
        } else if (current) {
            if (current.contains(document.activeElement)) button.focus({ preventScroll: true });
            current.remove();
        } else if (next) {
            card.append(document.importNode(next, true));
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
        status.textContent = "Обновляем статус...";
        try {
            const response = await fetch("/play", {
                method: "GET", credentials: "omit", cache: "no-store", redirect: "error", signal: active.signal
            });
            const html = await readPage(response);
            if (stopped || document.hidden || active.signal.aborted) return;
            const nextCard = new DOMParser().parseFromString(html, "text/html").querySelector(".play-page .join-panel");
            const { next, connection, age } = validate(nextCard);
            updateConnection(connection);
            card.querySelector("#join-title").textContent = nextCard.querySelector("#join-title").textContent;
            card.className = `join-panel join-panel--${next.dataset.state}`;
            panel.replaceWith(document.importNode(next, true));
            panel = card.querySelector("[data-play-status]");
            observedAt = performance.now();
            initialAge = age;
            updateAge();
            status.textContent = "Проверка завершена.";
        } catch {
            if (!stopped && !document.hidden && active.signal.reason !== "hidden") {
                markUnconfirmed("Не удалось обновить статус. Последняя проверка больше не подтверждает текущую доступность.");
                status.textContent = "Не удалось обновить статус. Повторим через минуту; можно обновить вручную.";
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
