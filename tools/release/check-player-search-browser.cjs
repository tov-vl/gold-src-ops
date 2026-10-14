const path = require('node:path');
const {parseArgs} = require('node:util');

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

async function check(baseUrl, playwright) {
    const browser = await playwright.chromium.launch({headless: true});
    const checks = [], failures = [], completed = new Set();
    let errors = 0;
    const deadline = setTimeout(() => { void browser.close().catch(() => {}); }, 90000);
    try {
        for (const scripts of [true, false]) for (const language of ['ru', 'en']) {
            const context = await browser.newContext({javaScriptEnabled: scripts, viewport: {width: 390, height: 844}});
            const page = await context.newPage();
            let posts = 0;
            page.on('pageerror', () => errors++);
            page.on('console', m => { if (m.type() === 'error') errors++; });
            page.on('requestfailed', r => failures.push({request: r, error: r.failure()?.errorText}));
            page.on('request', r => {
                const url = new URL(r.url());
                if (url.pathname.startsWith('/api/') || [...url.searchParams.keys()].some(k => k !== 'lang')) errors++;
                if (r.method() === 'POST') posts++;
            });
            const url = baseUrl + '/leaderboard' + (language === 'en' ? '?lang=en' : '');
            const response = await page.goto(url, {waitUntil: 'load'});
            if (response.status() !== 200 || !hasNoStore((await response.allHeaders())['cache-control']) ||
                await page.locator('html').getAttribute('lang') !== language) throw new Error('Page contract failed');
            const rows = page.locator('.leaderboard-table tbody tr');
            if (!await rows.count()) throw new Error('Saved row required');
            // Existing names remain in memory; no page body, screenshot or query is recorded.
            const name = (await rows.first().locator('th[scope=row]').innerText()).trim();
            await page.locator('#player-search-name').fill(name);
            const [posted] = await Promise.all([page.waitForNavigation({waitUntil: 'load'}),
                page.locator('#player-search-name').press('Enter')]);
            if (posted.status() !== 200 || !hasNoStore((await posted.allHeaders())['cache-control']) ||
                await page.locator('#player-search-message').getAttribute('data-search-state') !== 'fresh')
                throw new Error('Search contract failed');
            const names = await page.locator('.player-search-result h3').allTextContents();
            if (!names.length || names.length > 10 || names.some(x => x !== name) || page.url() !== url + '#player-search')
                throw new Error('Search correlation failed');
            for (const width of [320, 390, 1440]) {
                await page.setViewportSize({width, height: 900});
                await page.evaluate(() => document.documentElement.style.fontSize = '200%');
                if (await page.evaluate(() => document.documentElement.scrollWidth > document.documentElement.clientWidth))
                    throw new Error('Horizontal overflow');
            }
            await page.evaluate(() => document.documentElement.style.fontSize = '');
            if (scripts) completed.add(await refresh(page));
            if (posts !== 1 || await page.locator('#player-search-name').inputValue() !== name ||
                await page.locator('#player-search-message').getAttribute('data-search-state') !== 'fresh' ||
                await page.locator('.player-search-result').count() !== names.length) throw new Error('Refresh changed search');
            checks.push({language, scripts, result: 'passed', widths: [320, 390, 1440], text200: true});
            await context.close();
        }
        if (errors || unexpectedFailures(failures, completed).length) throw new Error('Browser failure');
        return {result: 'browser-acceptance-passed', checks, completedRefreshCancellations: failures.length,
            noQueryInUrl: true, noBrowserApiCalls: true};
    } finally {
        clearTimeout(deadline);
        await browser.close();
    }
}

module.exports = {hasNoStore, unexpectedFailures, refresh, check};
if (require.main === module) (async () => {
    const {values} = parseArgs({options: {'base-url': {type: 'string', default: 'https://goldsrcops.com'},
        'playwright-package': {type: 'string'}, execute: {type: 'boolean', default: false}}});
    const url = new URL(values['base-url']);
    if (url.protocol !== 'https:' || url.username || url.password || url.pathname !== '/' || url.search || url.hash)
        throw new Error('HTTPS origin required');
    if (!values.execute) return console.log(JSON.stringify({result: 'validated-plan', network: false}));
    const packagePath = values['playwright-package'] || path.resolve(__dirname,
        '../../tests/GoldSrcOps.WebTests/bin/Debug/net10.0/.playwright/package');
    console.log(JSON.stringify(await check(url.origin, require(packagePath))));
})().catch(() => { console.error('BROWSER_ACCEPTANCE=failed; inspect the fixed boundary before another check'); process.exitCode = 1; });
