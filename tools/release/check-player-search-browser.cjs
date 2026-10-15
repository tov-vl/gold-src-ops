const path = require('node:path');
const {parseArgs} = require('node:util');
const {randomUUID} = require('node:crypto');

const limits = Object.freeze({posts: 10, requests: 256, runMs: 120000, operationMs: 15000});

function hasNoStore(value) {
    return typeof value === 'string' && value.split(',').some(x => x.trim().toLowerCase() === 'no-store');
}

function unexpectedFailures(failures, completedRefreshes) {
    // A Chromium cancellation is acceptable only for this exact request after
    // the application consumed its full body and reported a successful refresh.
    return failures.filter(x => x.error !== 'net::ERR_ABORTED' || !completedRefreshes.has(x.request));
}

async function refresh(page) {
    const [response] = await Promise.all([
        page.waitForResponse(r => new URL(r.url()).pathname === '/leaderboard' &&
            r.request().method() === 'GET' && r.request().resourceType() === 'fetch', {timeout: 15000}),
        page.locator('[data-leaderboard-refresh]').click()
    ]);
    if (response.status() !== 200 || !hasNoStore((await response.allHeaders())['cache-control']))
        throw new Error('Refresh response failed');
    // response.finished() can remain pending after the bounded reader cancels a
    // consumed stream. Require the application's successful full-body parse.
    await page.waitForFunction(() => {
        const root = document.querySelector('.leaderboard-page');
        return document.querySelector('#leaderboard-refresh-status')?.textContent === root?.dataset.leaderboardDone &&
            !document.querySelector('[data-leaderboard-panel]')?.hasAttribute('aria-busy');
    }, {}, {timeout: 15000});
    return response.request();
}

// Fetch once without following redirects, then give the original response to the
// browser. A redirect must not bypass the origin or POST budget in route.continue.
function createRequestGuard(origin, budget = limits) {
    let posts = 0, requests = 0, failed = false;
    return {
        get posts() { return posts; },
        get requests() { return requests; },
        stop() { failed = true; },
        verify() { if (failed) throw new Error('Request boundary failed'); },
        async route(route) {
            let response;
            try {
                const request = route.request(), url = new URL(request.url());
                const post = request.method() === 'POST';
                if (failed || requests >= budget.requests || url.origin !== origin || url.username || url.password ||
                    (url.pathname === '/api' || url.pathname.startsWith('/api/')) ||
                    [...url.searchParams.keys()].some(k => k !== 'lang') ||
                    !['GET', 'POST'].includes(request.method()) ||
                    (post && (url.pathname !== '/leaderboard' || posts >= budget.posts))) {
                    failed = true;
                    await route.abort();
                    return;
                }
                requests++;
                if (post) posts++;
                response = await route.fetch({maxRedirects: 0, maxRetries: 0, timeout: limits.operationMs});
                if (response.status() >= 300 && response.status() < 400) throw new Error('Redirect refused');
                await route.fulfill({response});
            } catch {
                failed = true;
                await route.abort().catch(() => {});
            } finally {
                await response?.dispose().catch(() => { failed = true; });
            }
        }
    };
}

function retrySeconds(value) {
    if (!/^(?:[1-9]|10)$/.test(value ?? '')) throw new Error('Invalid retry duration');
    return Number(value);
}

async function submit(page, name, url) {
    await page.locator('#player-search-name').fill(name);
    const [response] = await Promise.all([page.waitForNavigation({waitUntil: 'load'}),
        page.locator('#player-search-name').press('Enter')]);
    if (response.status() !== 200 || !hasNoStore((await response.allHeaders())['cache-control']) ||
        page.url() !== url + '#player-search') throw new Error('Search response failed');
    return page.locator('#player-search-message').getAttribute('data-search-state');
}

async function countdown(page, url, guard, completed) {
    let name = 'check-' + randomUUID().replaceAll('-', '').slice(0, 20);
    let state = await submit(page, name, url);
    // The first new name can succeed when the ordinary search was cached.
    // Only that fresh result permits one additional probe, never another busy retry.
    if (state === 'fresh') { name += 'x'; state = await submit(page, name, url); }
    if (state !== 'busy') throw new Error('Natural busy response required');
    const seconds = retrySeconds(await page.locator('#player-search').getAttribute('data-search-retry-seconds'));
    const button = page.locator('#player-search button[type=submit]');
    const message = page.locator('#player-search-message');
    const ready = await page.locator('#player-search').getAttribute('data-search-ready');
    if (!ready || !await button.isDisabled() || await message.getAttribute('role') !== 'timer' ||
        await message.getAttribute('aria-live') !== 'off') throw new Error('Countdown initialization failed');
    const waitingPosts = guard.posts;
    await page.locator('#player-search-name').press('Enter');
    await page.waitForTimeout(250);
    if (guard.posts !== waitingPosts) throw new Error('Early Enter submitted');
    completed.add(await refresh(page));
    if (await page.locator('#player-search-name').inputValue() !== name) throw new Error('Countdown input changed');
    await page.waitForFunction(() => document.querySelector('#player-search button[type=submit]')?.disabled === false,
        {}, {timeout: (seconds + 2) * 1000});
    if (await message.textContent() !== ready ||
        await page.locator('[data-search-announcement]').textContent() !== ready ||
        guard.posts !== waitingPosts) throw new Error('Countdown completion failed');
    if (await submit(page, name, url) !== 'fresh' || guard.posts !== waitingPosts + 1)
        throw new Error('Manual retry failed');
    return {retrySeconds: seconds, manualRetry: true, noAutomaticLookup: true};
}

async function check(baseUrl, playwright, channel) {
    const browser = await playwright.chromium.launch({headless: true, timeout: limits.operationMs, ...(channel ? {channel} : {})});
    const checks = [], failures = [], completed = new Set();
    const guard = createRequestGuard(baseUrl);
    let errors = 0, timedOut = false;
    const deadline = setTimeout(() => { timedOut = true; guard.stop(); void browser.close().catch(() => {}); }, limits.runMs);
    try {
        for (const scripts of [true, false]) for (const language of ['ru', 'en']) {
            const context = await browser.newContext({javaScriptEnabled: scripts, serviceWorkers: 'block', viewport: {width: 390, height: 844}});
            await context.route('**/*', route => guard.route(route));
            const page = await context.newPage();
            page.setDefaultTimeout(limits.operationMs);
            page.setDefaultNavigationTimeout(limits.operationMs);
            page.on('pageerror', () => errors++);
            page.on('console', m => { if (m.type() === 'error') errors++; });
            page.on('requestfailed', r => failures.push({request: r, error: r.failure()?.errorText}));
            const before = guard.posts;
            const url = baseUrl + '/leaderboard' + (language === 'en' ? '?lang=en' : '');
            const response = await page.goto(url, {waitUntil: 'load'});
            if (response.status() !== 200 || !hasNoStore((await response.allHeaders())['cache-control']) ||
                await page.locator('html').getAttribute('lang') !== language) throw new Error('Page contract failed');
            const rows = page.locator('.leaderboard-table tbody tr');
            if (!await rows.count()) throw new Error('Saved row required');
            // Existing names remain in memory; no page body, screenshot or query is recorded.
            const name = (await rows.first().locator('th[scope=row]').innerText()).trim();
            if (await submit(page, name, url) !== 'fresh') throw new Error('Search contract failed');
            const names = await page.locator('.player-search-result h3').allTextContents();
            if (!names.length || names.length > 10 || names.some(x => x !== name)) throw new Error('Search correlation failed');
            for (const width of [320, 390, 1440]) {
                await page.setViewportSize({width, height: 900});
                await page.evaluate(() => document.documentElement.style.fontSize = '200%');
                if (await page.evaluate(() => document.documentElement.scrollWidth > document.documentElement.clientWidth))
                    throw new Error('Horizontal overflow');
            }
            await page.evaluate(() => document.documentElement.style.fontSize = '');
            if (scripts) completed.add(await refresh(page));
            if (guard.posts !== before + 1 || await page.locator('#player-search-name').inputValue() !== name ||
                await page.locator('#player-search-message').getAttribute('data-search-state') !== 'fresh' ||
                await page.locator('.player-search-result').count() !== names.length) throw new Error('Refresh changed search');
            const retry = scripts ? await countdown(page, url, guard, completed) : undefined;
            if (await page.evaluate(() => localStorage.length + sessionStorage.length) !== 0)
                throw new Error('Unexpected browser storage');
            guard.verify();
            if (errors || unexpectedFailures(failures, completed).length) throw new Error('Browser failure');
            checks.push({language, scripts, result: 'passed', widths: [320, 390, 1440], text200: true,
                posts: guard.posts - before, ...(retry ? {countdown: retry} : {})});
            await context.close();
            // Separate scenarios without retrying a failed request or changing server policy.
            if (scripts) await new Promise(resolve => setTimeout(resolve, 11000));
        }
        guard.verify();
        if (timedOut || errors || unexpectedFailures(failures, completed).length) throw new Error('Browser failure');
        return {result: 'browser-acceptance-passed', checks, completedRefreshCancellations: failures.length,
            posts: guard.posts, requests: guard.requests, limits, noQueryInUrl: true, noBrowserApiCalls: true};
    } finally {
        clearTimeout(deadline);
        await browser.close();
    }
}

module.exports = {hasNoStore, unexpectedFailures, refresh, retrySeconds, createRequestGuard, check};
if (require.main === module) (async () => {
    const {values} = parseArgs({options: {'base-url': {type: 'string', default: 'https://goldsrcops.com'},
        'playwright-package': {type: 'string'}, 'browser-channel': {type: 'string'}, execute: {type: 'boolean', default: false}}});
    const url = new URL(values['base-url']);
    if (url.protocol !== 'https:' || url.username || url.password || url.pathname !== '/' || url.search || url.hash)
        throw new Error('HTTPS origin required');
    if (values['browser-channel'] && !['chrome', 'msedge'].includes(values['browser-channel'])) throw new Error('Unsupported browser channel');
    if (!values.execute) return console.log(JSON.stringify({result: 'validated-plan', network: false, limits}));
    const packagePath = values['playwright-package'] || path.resolve(__dirname,
        '../../tests/GoldSrcOps.WebTests/bin/Debug/net10.0/.playwright/package');
    console.log(JSON.stringify(await check(url.origin, require(packagePath), values['browser-channel'])));
})().catch(() => { console.error('BROWSER_ACCEPTANCE=failed; inspect the fixed boundary before another check'); process.exitCode = 1; });
