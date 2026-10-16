const path = require('node:path');
const assert = require('node:assert/strict');
const {parseArgs} = require('node:util');
const {randomUUID} = require('node:crypto');

const limits = Object.freeze({posts: 10, requests: 256, runMs: 120000, operationMs: 15000});
const partialLimits = Object.freeze({...limits, posts: 14, runMs: 180000});
const emptyFallbackLimits = Object.freeze({...limits, runMs: 180000});

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
function createRequestGuard(origin, budget = limits, validateRequest = () => true) {
    let posts = 0, requests = 0, failed = false, firstFailure = null;
    const active = new Set();
    function refuse(phase, category) {
        failed = true;
        firstFailure ??= Object.freeze({phase, category});
    }
    function verify() { if (failed) throw new Error('Request boundary failed'); }
    async function handle(route) {
        let response, phase = 'policy';
        try {
            const request = route.request(), url = new URL(request.url());
            const post = request.method() === 'POST';
            if (failed || requests >= budget.requests || url.origin !== origin || url.username || url.password ||
                (url.pathname === '/api' || url.pathname.startsWith('/api/')) ||
                [...url.searchParams.keys()].some(k => k !== 'lang') ||
                !['GET', 'POST'].includes(request.method()) ||
                (post && (url.pathname !== '/leaderboard' || posts >= budget.posts)) || !validateRequest(request)) {
                refuse(phase, 'refused');
                await route.abort();
                return;
            }
            requests++;
            if (post) posts++;
            phase = 'fetch';
            response = await route.fetch({maxRedirects: 0, maxRetries: 0, timeout: limits.operationMs});
            phase = 'redirect';
            if (response.status() >= 300 && response.status() < 400) throw new Error('Redirect refused');
            phase = 'fulfill';
            await route.fulfill({response});
        } catch (error) {
            refuse(phase, failureCategory(error));
            await route.abort().catch(() => {});
        } finally {
            await response?.dispose().catch(error => { refuse('dispose', failureCategory(error)); });
        }
    }
    return {
        get posts() { return posts; },
        get requests() { return requests; },
        get failed() { return failed; },
        get firstFailure() { return firstFailure; },
        stop() { failed = true; },
        verify,
        route(route) {
            const task = handle(route);
            active.add(task);
            task.then(() => active.delete(task), () => active.delete(task));
            return task;
        },
        async drain() {
            let deadline;
            try {
                await Promise.race([
                    (async () => { while (active.size) await Promise.all([...active]); })(),
                    new Promise((_, reject) => { deadline = setTimeout(() => {
                        refuse('drain', 'timeout');
                        reject(new Error('Route drain timed out'));
                    }, budget.operationMs ?? limits.operationMs); })
                ]);
                verify();
            } finally { clearTimeout(deadline); }
        }
    };
}

async function closeContext(context, guard) {
    // A loaded/rendered response can still have a route handler disposing it.
    // Keep interception installed and the context alive until those handlers finish.
    await guard.drain();
    await context.close();
    // A late failure belongs to this scenario, not the next language's navigation.
    await guard.drain();
}

function retrySeconds(value) {
    if (!/^(?:[1-9]|10)$/.test(value ?? '')) throw new Error('Invalid retry duration');
    return Number(value);
}

async function submit(page, name, url, diagnostic) {
    await page.locator('#player-search-name').fill(name);
    const [response] = await Promise.all([page.waitForNavigation({waitUntil: 'load'}),
        page.locator('#player-search-name').press('Enter')]);
    if (response.status() !== 200 || !hasNoStore((await response.allHeaders())['cache-control']) ||
        page.url() !== url + '#player-search') throw new Error('Search response failed');
    const state = await page.locator('#player-search-message').getAttribute('data-search-state');
    diagnostic.outcomes[['fresh', 'busy', 'invalid', 'unavailable'].includes(state) ? state : 'unexpected']++;
    return state;
}

async function countdown(page, url, guard, completed, diagnostic, partial) {
    diagnostic.stage = 'countdown-probe';
    let name = 'check-' + randomUUID().replaceAll('-', '').slice(0, 20);
    let state = await submit(page, name, url, diagnostic);
    // The first new name can succeed when the ordinary search was cached.
    // Only that fresh result permits one additional probe, never another busy retry.
    if (state === 'fresh') { name += 'x'; state = await submit(page, name, url, diagnostic); }
    if (state !== 'busy') throw new Error('Natural busy response required');
    diagnostic.stage = 'countdown-initialization';
    const seconds = retrySeconds(await page.locator('#player-search').getAttribute('data-search-retry-seconds'));
    const button = page.locator('#player-search button[type=submit]');
    const message = page.locator('#player-search-message');
    const ready = await page.locator('#player-search').getAttribute('data-search-ready');
    if (!ready || !await button.isDisabled() || await message.getAttribute('role') !== 'timer' ||
        await message.getAttribute('aria-live') !== 'off') throw new Error('Countdown initialization failed');
    const waitingPosts = guard.posts;
    const timerStarted = Date.now();
    diagnostic.stage = 'countdown-mode-change';
    if (partial) {
        await page.locator('#player-search-match').selectOption('exact');
        diagnostic.mode = 'exact';
    }
    if (!await button.isDisabled()) throw new Error('Mode changed shared cooldown');
    await page.locator('#player-search-name').press('Enter');
    await page.waitForTimeout(250);
    if (guard.posts !== waitingPosts) throw new Error('Early Enter submitted');
    diagnostic.stage = 'countdown-refresh';
    completed.add(await refresh(page));
    if (await page.locator('#player-search-name').inputValue() !== name || (partial && await page.locator('#player-search-match').inputValue() !== 'exact')) throw new Error('Countdown input or mode changed');
    diagnostic.stage = 'countdown-completion';
    await page.waitForFunction(() => document.querySelector('#player-search button[type=submit]')?.disabled === false,
        {}, {timeout: (seconds + 2) * 1000});
    if (Date.now() - timerStarted > (seconds + 2) * 1000) throw new Error('Mode reset countdown');
    if (await message.textContent() !== ready ||
        await page.locator('[data-search-announcement]').textContent() !== ready ||
        guard.posts !== waitingPosts) throw new Error('Countdown completion failed');
    diagnostic.stage = 'countdown-manual-retry';
    if (await submit(page, name, url, diagnostic) !== 'fresh' || guard.posts !== waitingPosts + 1)
        throw new Error('Manual retry failed');
    return {retrySeconds: seconds, manualRetry: true, noAutomaticLookup: true};
}

function failureCategory(error) {
    return error?.name === 'TimeoutError' ? 'timeout' : 'check-failed';
}

async function check(baseUrl, playwright, channel, partial = false) {
    const budget = partial ? partialLimits : limits;
    const modes = partial ? ['exact', 'contains'] : ['exact'];
    const started = Date.now();
    const diagnostic = {stage: 'browser-launch', language: null, scripts: null, mode: null, width: null,
        outcomes: {fresh: 0, busy: 0, invalid: 0, unavailable: 0, unexpected: 0}};
    const checks = [], failures = [], completed = new Set();
    const guard = createRequestGuard(baseUrl, budget);
    let pageErrors = 0, consoleErrors = 0, timedOut = false, browser, deadline, result;
    const report = () => ({...diagnostic, outcomes: {...diagnostic.outcomes}, checks: [...checks],
        posts: guard.posts, requests: guard.requests, requestBoundaryFailed: guard.failed, firstRequestFailure: guard.firstFailure, pageErrors, consoleErrors,
        unexpectedRequestFailures: unexpectedFailures(failures, completed).length,
        completedRefreshCancellations: failures.length - unexpectedFailures(failures, completed).length,
        timedOut, durationMs: Date.now() - started, limits: budget});
    try {
        browser = await playwright.chromium.launch({headless: true, timeout: limits.operationMs, ...(channel ? {channel} : {})});
        deadline = setTimeout(() => { timedOut = true; guard.stop(); void browser.close().catch(() => {}); }, budget.runMs);
        for (const scripts of [true, false]) for (const language of ['ru', 'en']) {
            Object.assign(diagnostic, {stage: 'context', language, scripts, mode: null, width: 390});
            const context = await browser.newContext({javaScriptEnabled: scripts, serviceWorkers: 'block', viewport: {width: 390, height: 844}});
            await context.route('**/*', route => guard.route(route));
            const page = await context.newPage();
            page.setDefaultTimeout(limits.operationMs);
            page.setDefaultNavigationTimeout(limits.operationMs);
            page.on('pageerror', () => pageErrors++);
            page.on('console', m => { if (m.type() === 'error') consoleErrors++; });
            page.on('requestfailed', r => failures.push({request: r, error: r.failure()?.errorText}));
            const before = guard.posts;
            const url = baseUrl + '/leaderboard' + (language === 'en' ? '?lang=en' : '');
            diagnostic.stage = 'page-contract';
            const response = await page.goto(url, {waitUntil: 'load'});
            if (response.status() !== 200 || !hasNoStore((await response.allHeaders())['cache-control']) ||
                await page.locator('html').getAttribute('lang') !== language) throw new Error('Page contract failed');
            diagnostic.stage = 'saved-row';
            const rows = page.locator('.leaderboard-table tbody tr');
            if (!await rows.count()) throw new Error('Saved row required');
            // Existing names remain in memory; no page body, screenshot or query is recorded.
            const savedNames = await rows.locator('th[scope=row]').allTextContents();
            const name = savedNames.map(x => x.trim()).find(x => Array.from(x).length >= (partial ? 3 : 1));
            if (!name) throw new Error('Eligible saved name required');
            for (const [modeIndex, mode] of modes.entries()) {
                diagnostic.mode = mode; diagnostic.stage = 'mode-cooldown';
                if (modeIndex) await page.waitForTimeout(11000);
                const query = mode === 'exact' ? name : Array.from(name).slice(0, 3).join('');
                diagnostic.stage = 'search-submit';
                if (partial) await page.locator('#player-search-match').selectOption(mode);
                if (await submit(page, query, url, diagnostic) !== 'fresh') throw new Error('Search contract failed');
                diagnostic.stage = 'search-correlation';
                const names = await page.locator('.player-search-result h3').allTextContents();
                if (!names.length || names.length > 10 || !names.includes(name) || names.some(x => mode === 'exact' ? x !== query : !x.toLowerCase().includes(query.toLowerCase()))) throw new Error('Search correlation failed');
                for (const width of [320, 390, 1440]) {
                    diagnostic.stage = 'layout'; diagnostic.width = width;
                    await page.setViewportSize({width, height: 900});
                    await page.evaluate(() => document.documentElement.style.fontSize = '200%');
                    if (await page.evaluate(() => document.documentElement.scrollWidth > document.documentElement.clientWidth))
                        throw new Error('Horizontal overflow');
                }
                await page.evaluate(() => document.documentElement.style.fontSize = '');
                diagnostic.stage = 'search-refresh';
                if (scripts) completed.add(await refresh(page));
                diagnostic.stage = 'search-preservation';
                if (guard.posts !== before + modeIndex + 1 || await page.locator('#player-search-name').inputValue() !== query || (partial && await page.locator('#player-search-match').inputValue() !== mode) ||
                    await page.locator('#player-search-message').getAttribute('data-search-state') !== 'fresh' ||
                    await page.locator('.player-search-result').count() !== names.length) throw new Error('Refresh changed search');
            }
            const retry = scripts ? await countdown(page, url, guard, completed, diagnostic, partial) : undefined;
            diagnostic.stage = 'storage';
            if (await page.evaluate(() => localStorage.length + sessionStorage.length) !== 0)
                throw new Error('Unexpected browser storage');
            diagnostic.stage = 'browser-errors';
            guard.verify();
            if (pageErrors || consoleErrors || unexpectedFailures(failures, completed).length) throw new Error('Browser failure');
            diagnostic.stage = 'context-close';
            await closeContext(context, guard);
            if (pageErrors || consoleErrors || unexpectedFailures(failures, completed).length) throw new Error('Browser failure');
            checks.push({language, scripts, result: 'passed', widths: [320, 390, 1440], text200: true,
                modes, posts: guard.posts - before, ...(retry ? {countdown: retry} : {})});
            // Separate scenarios without retrying a failed request or changing server policy.
            if (scripts || language === 'ru') await new Promise(resolve => setTimeout(resolve, 11000));
        }
        guard.verify();
        if (timedOut || pageErrors || consoleErrors || unexpectedFailures(failures, completed).length) throw new Error('Browser failure');
        result = {result: 'browser-acceptance-passed', ...report(), noQueryInUrl: true, noBrowserApiCalls: true};
    } catch (error) {
        // Never serialize exception text, browser call logs, names, URLs or response bodies.
        result = {result: 'browser-acceptance-failed', category: failureCategory(error), ...report()};
    } finally {
        clearTimeout(deadline);
        guard.stop();
        try { await browser?.close(); }
        catch {
            if (result?.result === 'browser-acceptance-passed')
                result = {result: 'browser-acceptance-failed', category: 'cleanup-failed', ...report(), stage: 'cleanup'};
        }
    }
    return result;
}


function matchesSearchSubmission(request, expected) {
    const raw = request.postData() || '';
    if (!expected || Buffer.byteLength(raw, 'utf8') > 4096) return false;
    const fields = new URLSearchParams(raw);
    return ['Model.Name', 'Model.Match', '__RequestVerificationToken'].every(key => fields.getAll(key).length === 1) &&
        fields.getAll('Model.Action').length <= 1 && !!fields.get('__RequestVerificationToken') &&
        fields.get('Model.Name') === expected.name && fields.get('Model.Match') === expected.mode &&
        (fields.get('Model.Action') || null) === expected.action;
}

async function checkEmptyFallback(baseUrl, playwright, channel) {
    const started = Date.now(), checks = [];
    const diagnostic = {stage: 'browser-launch', language: null, scripts: null, mode: null, width: null,
        outcomes: {fresh: 0, busy: 0, invalid: 0, unavailable: 0, unexpected: 0}};
    let expected = null, browser, deadline, result, browserErrors = 0, timedOut = false;
    const guard = createRequestGuard(baseUrl, emptyFallbackLimits, request => {
        if (request.method() !== 'POST') return true;
        const valid = matchesSearchSubmission(request, expected);
        // Each deliberate keyboard activation permits exactly one matching POST.
        expected = null;
        return valid;
    });
    const report = () => ({...diagnostic, outcomes: {...diagnostic.outcomes}, checks: [...checks],
        posts: guard.posts, requests: guard.requests, requestBoundaryFailed: guard.failed,
        firstRequestFailure: guard.firstFailure, browserErrors, timedOut,
        durationMs: Date.now() - started, limits: emptyFallbackLimits});
    try {
        browser = await playwright.chromium.launch({headless: true, timeout: limits.operationMs, ...(channel ? {channel} : {})});
        deadline = setTimeout(() => { timedOut = true; guard.stop(); void browser.close().catch(() => {}); }, emptyFallbackLimits.runMs);
        for (const scripts of [true, false]) for (const language of ['ru', 'en']) {
            Object.assign(diagnostic, {stage: 'context', language, scripts, mode: 'exact', width: 390});
            const context = await browser.newContext({javaScriptEnabled: scripts, serviceWorkers: 'block', viewport: {width: 390, height: 844}});
            await context.route('**/*', route => guard.route(route));
            const page = await context.newPage();
            page.setDefaultTimeout(limits.operationMs);
            page.setDefaultNavigationTimeout(limits.operationMs);
            page.on('pageerror', () => browserErrors++);
            page.on('console', m => { if (m.type() === 'error') browserErrors++; });
            page.on('requestfailed', () => browserErrors++);
            const url = baseUrl + '/leaderboard' + (language === 'en' ? '?lang=en' : '');
            // A collision refuses the run. Do not regenerate a name or retry a failed scenario.
            const name = 'check-' + randomUUID().replaceAll('-', '').slice(0, 20);
            const before = guard.posts;
            diagnostic.stage = 'page-contract';
            const initial = await page.goto(url, {waitUntil: 'load'});
            assert.equal(initial.status(), 200);
            assert.ok(hasNoStore((await initial.allHeaders())['cache-control']));
            assert.equal(await page.locator('html').getAttribute('lang'), language);
            const input = page.locator('#player-search-name'), mode = page.locator('#player-search-match');
            const button = page.locator('#player-search-submit'), fallback = page.locator('#player-search-contains');
            assert.equal(await fallback.count(), 0);
            assert.equal(await mode.inputValue(), 'exact');
            async function post(action, modeValue, invoke) {
                expected = {name, mode: modeValue, action};
                const [response] = await Promise.all([page.waitForNavigation({waitUntil: 'load'}), invoke()]);
                expected = null;
                guard.verify();
                assert.equal(response.status(), 200);
                assert.ok(hasNoStore((await response.allHeaders())['cache-control']));
                assert.equal(page.url(), url + '#player-search');
                const state = await page.locator('#player-search-message').getAttribute('data-search-state');
                diagnostic.outcomes[Object.hasOwn(diagnostic.outcomes, state) ? state : 'unexpected']++;
                assert.equal(await input.inputValue(), name);
                return state;
            }
            diagnostic.stage = 'empty-exact';
            await input.fill(name);
            assert.equal(guard.posts, before);
            assert.equal(await post(null, 'exact', () => input.press('Enter')), 'fresh');
            assert.equal(await page.locator('.player-search-result').count(), 0);
            assert.equal(await fallback.textContent(), language === 'en' ? 'Search by part of nickname' : 'Искать по части ника');
            assert.equal(guard.posts, before + 1);
            if (!scripts) {
                // Keep geometry work outside the short natural busy window.
                diagnostic.stage = 'layout';
                for (const width of [320, 390, 1440]) {
                    diagnostic.width = width;
                    await page.setViewportSize({width, height: 900});
                    await page.evaluate(() => document.documentElement.style.fontSize = '200%');
                    assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth));
                }
                await page.evaluate(() => document.documentElement.style.fontSize = '');
                await page.waitForTimeout(11000);
            }
            diagnostic.stage = 'fallback-keyboard';
            diagnostic.mode = 'contains';
            await button.focus();
            await button.press('Tab');
            assert.ok(await fallback.evaluate(el => el === document.activeElement));
            const state = await post('contains', 'exact', () => fallback.press('Enter'));
            assert.equal(guard.posts, before + 2);
            assert.equal(await mode.inputValue(), 'contains');
            assert.equal(await fallback.count(), 0);
            if (scripts) {
                diagnostic.stage = 'busy';
                assert.equal(state, 'busy');
                const seconds = retrySeconds(await page.locator('#player-search').getAttribute('data-search-retry-seconds'));
                assert.ok(await button.isDisabled());
                const ready = await page.locator('#player-search').getAttribute('data-search-ready');
                assert.ok(ready);
                await input.press('Enter');
                await page.waitForTimeout(250);
                assert.equal(guard.posts, before + 2);
                await page.waitForFunction(() => document.querySelector('#player-search-submit')?.disabled === false,
                    {}, {timeout: (seconds + 2) * 1000, polling: 100});
                assert.equal(await page.locator('#player-search-message').textContent(), ready);
                assert.equal(await page.locator('[data-search-announcement]').textContent(), ready);
                assert.equal(guard.posts, before + 2);
                diagnostic.stage = 'manual-retry';
                assert.equal(await post(null, 'contains', () => input.press('Enter')), 'fresh');
                assert.equal(guard.posts, before + 3);
            } else assert.equal(state, 'fresh');
            assert.equal(await page.locator('.player-search-result').count(), 0);
            assert.equal(await fallback.count(), 0);
            assert.equal(await mode.inputValue(), 'contains');
            diagnostic.stage = 'privacy';
            assert.equal(await page.evaluate(() => localStorage.length + sessionStorage.length), 0);
            assert.ok(!page.url().includes(name));
            assert.equal(browserErrors, 0);
            guard.verify();
            diagnostic.stage = 'context-close';
            await closeContext(context, guard);
            assert.equal(browserErrors, 0);
            guard.verify();
            checks.push({language, scripts, posts: guard.posts - before, result: 'passed', keyboard: true,
                ...(!scripts ? {widths: [320, 390, 1440], text200: true} : {countdown: true, manualRetry: true})});
            if (checks.length < 4) await new Promise(resolve => setTimeout(resolve, 11000));
        }
        assert.equal(guard.posts, 10);
        assert.deepEqual(diagnostic.outcomes, {fresh: 8, busy: 2, invalid: 0, unavailable: 0, unexpected: 0});
        assert.equal(browserErrors, 0);
        guard.verify();
        assert.ok(!timedOut);
        diagnostic.stage = 'complete';
        result = {result: 'browser-acceptance-passed', ...report()};
    } catch (error) {
        result = {result: 'browser-acceptance-failed', category: failureCategory(error), ...report()};
    } finally {
        clearTimeout(deadline);
        guard.stop();
        try { await browser?.close(); }
        catch {
            if (result?.result === 'browser-acceptance-passed')
                result = {result: 'browser-acceptance-failed', category: 'cleanup-failed', ...report(), stage: 'cleanup'};
        }
    }
    return result;
}

module.exports = {hasNoStore, unexpectedFailures, refresh, retrySeconds, createRequestGuard, failureCategory, closeContext, matchesSearchSubmission, checkEmptyFallback, check};
if (require.main === module) (async () => {
    const {values} = parseArgs({options: {'base-url': {type: 'string', default: 'https://goldsrcops.com'},
        'playwright-package': {type: 'string'}, 'browser-channel': {type: 'string'}, partial: {type: 'boolean', default: false},
        'empty-fallback': {type: 'boolean', default: false}, execute: {type: 'boolean', default: false}}});
    if (values.partial && values['empty-fallback']) throw new Error('Choose one acceptance scenario');
    const budget = values['empty-fallback'] ? emptyFallbackLimits : values.partial ? partialLimits : limits;
    const url = new URL(values['base-url']);
    if (url.protocol !== 'https:' || url.username || url.password || url.pathname !== '/' || url.search || url.hash)
        throw new Error('HTTPS origin required');
    if (values['browser-channel'] && !['chrome', 'msedge'].includes(values['browser-channel'])) throw new Error('Unsupported browser channel');
    if (!values.execute) return console.log(JSON.stringify({result: 'validated-plan', network: false, limits: budget}));
    const packagePath = values['playwright-package'] || path.resolve(__dirname,
        '../../tests/GoldSrcOps.WebTests/bin/Debug/net10.0/.playwright/package');
    const run = values['empty-fallback'] ? checkEmptyFallback : check;
    const result = await run(url.origin, require(packagePath), values['browser-channel'], values.partial);
    console.log(JSON.stringify(result));
    if (result.result !== 'browser-acceptance-passed') process.exitCode = 1;
})().catch(() => { console.error('BROWSER_ACCEPTANCE=failed; inspect the fixed boundary before another check'); process.exitCode = 1; });
