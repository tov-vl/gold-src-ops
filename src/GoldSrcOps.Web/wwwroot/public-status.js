(() => {
    const page = document.querySelector(".status-page");
    const button = page?.querySelector("[data-status-refresh]");
    const status = page?.querySelector("#status-refresh-message");
    const selectors = ["[data-status-fleet]", "[data-status-server]", "[data-status-history]"];
    if (!button || !status || selectors.some(selector => !page.querySelector(selector))) return;

    const interval = 60_000;
    const timeoutMs = 8_000;
    const maximumBytes = 131_072;
    const url = `/?range=${page.dataset.window === "7d" ? "7d" : "24h"}${page.lang === "en" ? "&lang=en" : ""}`;
    let controller = null;
    let stopped = false;
    let lastRequest = -Infinity;
    let nextRefresh = performance.now() + interval;
    let ageBase = performance.now();
    let timer;

    function unconfirm(panel) {
        if (panel.dataset.state === "unavailable") return;
        panel.dataset.state = "unknown";
        panel.dataset.stale = "true";
        const warning = panel.querySelector("[data-status-warning]");
        if (warning) { warning.hidden = false; warning.textContent = page.dataset.snapshotWarning; }
        const summary = panel.querySelector(".status-summary");
        if (summary) {
            summary.className = "status-summary status-summary--unknown";
            summary.querySelector(".status-kicker").textContent = page.dataset.statusUnknown;
            summary.querySelector("h2").textContent = page.dataset.unconfirmed;
            summary.querySelector(".summary-copy div > p:last-child").textContent = page.dataset.unconfirmedDetail;
        }
        const server = panel.querySelector(".public-server");
        if (server) {
            server.className = "public-server public-server--unknown";
            const label = server.querySelector(".public-server__state");
            const signal = label.querySelector("span");
            label.replaceChildren(signal, page.dataset.statusUnknown);
            const facts = server.querySelectorAll(".public-server__facts dd");
            facts[0].textContent = page.dataset.noFreshData;
            facts[1].textContent = page.dataset.noFreshData;
        }
    }

    function updateAges() {
        for (const selector of selectors.slice(0, 2)) {
            const panel = page.querySelector(selector);
            if (panel.dataset.ageSeconds === "") continue;
            const age = Number(panel.dataset.ageSeconds) + Math.max(0, performance.now() - ageBase) / 1000;
            const label = panel.querySelector("[data-status-age]");
            if (label) label.textContent = page.dataset.age.replace("{0}", Math.ceil(age));
            if (age > 180) unconfirm(panel);
        }
    }

    async function readPage(response) {
        if (!response.ok || !response.headers.get("content-type")?.includes("text/html")) throw new Error();
        const reader = response.body.getReader();
        const decoder = new TextDecoder("utf-8", { fatal: true });
        let bytes = 0, html = "";
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

    function validate(nextPage) {
        if (nextPage?.lang !== page.lang || nextPage.dataset.window !== page.dataset.window) throw new Error();
        const panels = selectors.map(selector => {
            if (nextPage.querySelectorAll(selector).length !== 1) throw new Error();
            return nextPage.querySelector(selector);
        });
        const allowedTags = new Set(["DIV", "SECTION", "P", "SPAN", "STRONG", "H2", "TIME", "ARTICLE", "DL", "DT", "DD", "CODE", "BUTTON", "A", "I"]);
        for (const panel of panels) {
            for (const element of [panel, ...panel.querySelectorAll("*")]) {
                if (!allowedTags.has(element.tagName)) throw new Error();
                for (const attribute of element.attributes) {
                    if (!/^(class|id|role|style|hidden|datetime|title|type|href|aria-[a-z-]+|data-[a-z-]+|b-[a-z0-9]+)$/.test(attribute.name)) throw new Error();
                    if (attribute.name === "style" && !/^(flex-grow: \d+|height: \d+(\.\d+)?%;|--history-columns: \d+)$/.test(attribute.value)) throw new Error();
                }
            }
        }
        for (const panel of panels.slice(0, 2)) {
            const age = panel.dataset.ageSeconds === "" ? null : Number(panel.dataset.ageSeconds);
            if (!Object.hasOwn(panel.dataset, "ageSeconds") || (age !== null && (!Number.isFinite(age) || age < 0))) throw new Error();
            if (["operational", "degraded", "online", "offline"].includes(panel.dataset.state) && age === null) throw new Error();
            if (panel.dataset.state !== "unavailable" && (!panel.querySelector("[data-status-warning]") || !panel.querySelector("[data-status-age]"))) throw new Error();
        }
        const [fleet, server, history] = panels;
        if (!["operational", "degraded", "unknown", "unavailable"].includes(fleet.dataset.state) || !fleet.querySelector(".status-summary")) throw new Error();
        if (fleet.dataset.state !== "unavailable" && (!fleet.querySelector(".status-kicker") || !fleet.querySelector("h2") || !fleet.querySelector(".summary-copy div > p:last-child") || fleet.querySelectorAll(".metric").length !== 4)) throw new Error();
        if (!["online", "offline", "unknown", "unavailable"].includes(server.dataset.state) || !["true", "false"].includes(server.dataset.failed)) throw new Error();
        if (fleet.querySelector("a, button") || history.querySelector("a, button")) throw new Error();
        if (server.dataset.state === "unavailable") {
            if (server.querySelector(".public-server")) throw new Error();
        } else {
            const command = server.querySelector(".public-server__connect code")?.textContent;
            if (!server.querySelector(".public-server") || !server.querySelector("#public-server-copy-status") || !server.querySelector("#public-server-title") || !server.querySelector(".public-server__state span") ||
                server.querySelectorAll(".public-server__facts dd").length !== 3 ||
                !command?.startsWith("connect ") || command.length > 300 ||
                server.querySelector("[data-copy-connect]")?.dataset.copyConnect !== command ||
                server.querySelector(".public-server__steam")?.getAttribute("href") !== `steam://${command.replace("connect ", "connect/")}` ||
                server.querySelector(".public-server__connect > a")?.getAttribute("href") !== (page.lang === "en" ? "/play?lang=en" : "/play") ||
                server.querySelectorAll("a").length !== 2 || server.querySelectorAll("button").length !== 1) throw new Error();
        }
        if (!["true", "false"].includes(history.dataset.available) ||
            (history.dataset.available === "true" ? history.querySelectorAll(".history-bar").length !== (page.dataset.window === "7d" ? 28 : 24) : !history.querySelector(".history-unavailable"))) throw new Error();
        return panels;
    }

    function apply(panels, startedAt) {
        const scrollX = window.scrollX, scrollY = window.scrollY;
        const focused = document.activeElement;
        const oldServer = page.querySelector(selectors[1]);
        const connection = oldServer.querySelector(".public-server__connect");
        const nextConnection = panels[1].querySelector(".public-server__connect");
        const partialFailure = panels[0].dataset.state === "unavailable" || panels[1].dataset.failed === "true" || panels[2].dataset.available === "false";
        // Keep connection controls as the same nodes, including clipboard feedback and focus.
        const imported = panels.map(panel => document.importNode(panel, true));
        if (connection && nextConnection) {
            const command = nextConnection.querySelector("code").textContent;
            const copy = connection.querySelector("[data-copy-connect]");
            if (copy.dataset.copyConnect !== command) connection.querySelector("#public-server-copy-status").textContent = "";
            connection.querySelector("code").textContent = command;
            copy.dataset.copyConnect = command;
            connection.querySelector(".public-server__steam").setAttribute("href", nextConnection.querySelector(".public-server__steam").getAttribute("href"));
            imported[1].querySelector(".public-server__connect").replaceWith(connection);
        }
        selectors.forEach((selector, index) => {
            const current = page.querySelector(selector);
            if (index === 1 && panels[1].dataset.failed === "true") {
                // Carry the old observation age forward when only this projection failed.
                if (current.dataset.ageSeconds !== "") current.dataset.ageSeconds = String(Number(current.dataset.ageSeconds) + Math.max(0, startedAt - ageBase) / 1000);
                unconfirm(current);
            } else current.replaceWith(imported[index]);
        });
        ageBase = startedAt;
        if (focused !== document.body && document.activeElement !== focused) {
            (focused.isConnected ? focused : button).focus({ preventScroll: true });
        }
        window.scrollTo(scrollX, scrollY);
        updateAges();
        status.textContent = partialFailure ? page.dataset.statusFailure : page.dataset.statusDone;
    }

    async function refresh() {
        if (stopped || document.hidden || controller || performance.now() - lastRequest < 5_000) return;
        lastRequest = performance.now();
        nextRefresh = lastRequest + interval;
        const active = new AbortController();
        controller = active;
        const timeout = setTimeout(() => active.abort(), timeoutMs);
        button.setAttribute("aria-disabled", "true");
        status.textContent = page.dataset.refreshingStatus;
        try {
            const response = await fetch(url, { method: "GET", credentials: "omit", cache: "no-store", redirect: "error", signal: active.signal });
            const html = await readPage(response);
            if (stopped || document.hidden || active.signal.aborted) return;
            const next = new DOMParser().parseFromString(html, "text/html").querySelector(".status-page");
            apply(validate(next), lastRequest);
        } catch {
            if (!stopped && !document.hidden && active.signal.reason !== "hidden") {
                selectors.slice(0, 2).forEach(selector => unconfirm(page.querySelector(selector)));
                status.textContent = page.dataset.statusFailure;
                updateAges();
            }
        } finally {
            clearTimeout(timeout);
            if (controller === active) controller = null;
            button.removeAttribute("aria-disabled");
        }
    }

    function tick() {
        if (stopped || document.hidden) return;
        updateAges();
        if (performance.now() >= nextRefresh) void refresh();
    }
    button.addEventListener("click", event => { event.preventDefault(); void refresh(); });
    document.addEventListener("visibilitychange", () => {
        if (document.hidden) { controller?.abort("hidden"); status.textContent = page.dataset.refreshPaused; }
        else { status.textContent = page.dataset.refreshActive; tick(); }
    });
    window.addEventListener("pagehide", () => { stopped = true; controller?.abort(); clearInterval(timer); });
    window.addEventListener("pageshow", () => {
        if (!stopped) return;
        stopped = false;
        timer = setInterval(tick, 1000);
        tick();
    });
    status.textContent = page.dataset.refreshActive;
    updateAges();
    timer = setInterval(tick, 1000);
})();
