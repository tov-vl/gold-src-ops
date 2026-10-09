document.addEventListener("click", async event => {
    const button = event.target.closest("[data-copy-connect]");
    if (!button) {
        return;
    }

    const command = button.dataset.copyConnect;
    const statusId = button.getAttribute("aria-describedby");
    const status = statusId ? document.getElementById(statusId) : null;
    if (!command || !status) {
        return;
    }

    try {
        await navigator.clipboard.writeText(command);
        status.textContent = button.dataset.copySuccess || "Connection command copied.";
    } catch {
        status.textContent = button.dataset.copyFailure || "Copy failed. Select the command manually.";
    }
});
