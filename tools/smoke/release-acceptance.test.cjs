const {test} = require('node:test');
const assert = require('node:assert/strict');
const {hasNoStore, unexpectedFailures, refresh, retrySeconds, createRequestGuard, closeContext, check} = require('../release/check-player-search-browser.cjs');

test('cache directives accept combined no-store without weakening missing-header refusal', () => {
    for (const value of ['no-store', 'no-cache, no-store', 'private, NO-STORE']) assert.ok(hasNoStore(value));
    for (const value of [undefined, '', 'no-cache', 'x-no-store', 'no-store=false', 'public, max-age=30']) assert.ok(!hasNoStore(value));
});

test('only the exact fully completed refresh can account for an abort', () => {
    const accepted = {}, other = {};
    const failures = [{request: accepted, error: 'net::ERR_ABORTED'}, {request: other, error: 'net::ERR_ABORTED'},
        {request: accepted, error: 'net::ERR_FAILED'}];
    assert.deepEqual(unexpectedFailures(failures, new Set([accepted])), failures.slice(1));
    assert.deepEqual(unexpectedFailures(failures, new Set()), failures);
});

test('refresh does not become accepted until successful full-body rendering', async () => {
    const request = {};
    let rendered = false;
    const response = {status: () => 200, allHeaders: async () => ({'cache-control': 'no-cache, no-store'}), request: () => request};
    const page = {waitForResponse: async () => response, locator: () => ({click: async () => {}}),
        waitForFunction: async () => { if (!rendered) throw new Error('incomplete response'); }};
    await assert.rejects(refresh(page), /incomplete response/);
    rendered = true;
    assert.equal(await refresh(page), request);
    response.status = () => 503;
    await assert.rejects(refresh(page), /Refresh response failed/);
});

function routedRequest(url, method = 'GET', status = 200) {
    const calls = [];
    return {calls, request: () => ({url: () => url, method: () => method}),
        fetch: async options => { calls.push(['fetch', options]); return {status: () => status,
            dispose: async () => calls.push(['dispose'])}; },
        fulfill: async () => calls.push(['fulfill']), abort: async () => calls.push(['abort'])};
}

test('request guard refuses unexpected destinations and query data before network access', async () => {
    for (const [url, method] of [
        ['https://other.example/leaderboard', 'GET'],
        ['https://user:secret@site.example/leaderboard', 'GET'],
        ['https://site.example/api', 'GET'], ['https://site.example/api/players', 'GET'],
        ['https://site.example/leaderboard?name=private', 'GET'],
        ['https://site.example/other', 'POST'], ['https://site.example/leaderboard', 'PUT']
    ]) {
        const guard = createRequestGuard('https://site.example');
        const route = routedRequest(url, method);
        await guard.route(route);
        assert.deepEqual(route.calls, [['abort']]);
        assert.throws(() => guard.verify(), /Request boundary failed/);
        assert.equal(guard.requests, 0);
    }
});

test('POST and total request budgets stop before an extra request and stay closed', async () => {
    for (const budget of [{posts: 1, requests: 5}, {posts: 5, requests: 1}]) {
        const guard = createRequestGuard('https://site.example', budget);
        const first = routedRequest('https://site.example/leaderboard?lang=en', 'POST');
        await guard.route(first);
        guard.verify();
        assert.deepEqual(first.calls, [['fetch', {maxRedirects: 0, maxRetries: 0, timeout: 15000}], ['fulfill'], ['dispose']]);
        const extra = routedRequest('https://site.example/leaderboard', 'POST');
        await guard.route(extra);
        assert.deepEqual(extra.calls, [['abort']]);
        const after = routedRequest('https://site.example/');
        await guard.route(after);
        assert.deepEqual(after.calls, [['abort']]);
        assert.equal(guard.posts, 1);
        assert.equal(guard.requests, 1);
        assert.throws(() => guard.verify(), /Request boundary failed/);
    }
});

test('redirects and transport failures terminate without following or retrying', async () => {
    for (const status of [301, 302, 307, 308, 'timeout']) {
        const guard = createRequestGuard('https://site.example');
        const route = routedRequest('https://site.example/leaderboard', 'POST', status);
        if (status === 'timeout') route.fetch = async options => {
            route.calls.push(['fetch', options]); throw new Error('timeout');
        };
        await guard.route(route);
        assert.deepEqual(route.calls, [['fetch', {maxRedirects: 0, maxRetries: 0, timeout: 15000}], ['abort'],
            ...(status === 'timeout' ? [] : [['dispose']])]);
        assert.throws(() => guard.verify(), /Request boundary failed/);
        assert.equal(guard.firstFailure.phase, status === 'timeout' ? 'fetch' : 'redirect');
    }
});

test('deadline refusal stops subsequent traffic before network access', async () => {
    const guard = createRequestGuard('https://site.example');
    guard.stop();
    const route = routedRequest('https://site.example/leaderboard');
    await guard.route(route);
    assert.deepEqual(route.calls, [['abort']]);
    assert.throws(() => guard.verify(), /Request boundary failed/);
});

test('retry duration accepts only the bounded server contract', () => {
    for (const value of ['1', '7', '10']) assert.equal(retrySeconds(value), Number(value));
    for (const value of [undefined, null, '', '0', '-1', '11', '1.5', ' 1', '01', 'NaN'])
        assert.throws(() => retrySeconds(value), /Invalid retry duration/);
});

test('CLI validates plans without loading Playwright or accepting an unsafe origin', () => {
    const {spawnSync} = require('node:child_process');
    const script = require.resolve('../release/check-player-search-browser.cjs');
    const run = args => spawnSync(process.execPath, [script, '--playwright-package', 'missing-fixture-package', ...args],
        {encoding: 'utf8', timeout: 5000});
    const dry = run(['--browser-channel', 'msedge']);
    assert.equal(dry.status, 0);
    assert.equal(JSON.parse(dry.stdout).network, false);
    assert.equal(JSON.parse(dry.stdout).limits.posts, 10);
    const partial = run(['--partial']);
    assert.equal(partial.status, 0);
    assert.equal(JSON.parse(partial.stdout).limits.posts, 14);
    assert.equal(JSON.parse(partial.stdout).limits.runMs, 180000);
    for (const args of [['--base-url', 'http://site.example'],
        ['--base-url', 'https://user:secret@site.example'], ['--base-url', 'https://site.example/path'],
        ['--base-url', 'https://site.example?name=private'], ['--base-url', 'https://site.example#fragment'],
        ['--browser-channel', 'unsupported']]) {
        const invalid = run(args);
        assert.equal(invalid.status, 1);
        assert.equal(invalid.stdout, '');
        assert.match(invalid.stderr, /^BROWSER_ACCEPTANCE=failed;/);
        assert.ok(!invalid.stderr.includes('private') && !invalid.stderr.includes('secret'));
    }
});

test('launch failures emit a bounded report without exception or call-log content', async () => {
    const sentinel = 'private-player https://secret.example/path?token=hidden';
    const report = await check('https://site.example', {chromium: {launch: async () => { throw new Error(sentinel); }}});
    assert.equal(report.result, 'browser-acceptance-failed');
    assert.equal(report.stage, 'browser-launch');
    assert.equal(report.category, 'check-failed');
    assert.equal(report.posts, 0);
    assert.equal(report.requests, 0);
    assert.deepEqual(report.checks, []);
    assert.ok(!JSON.stringify(report).includes(sentinel));
    assert.ok(!JSON.stringify(report).includes('secret.example'));
});

test('failed navigation retains scenario and actual request counts even if cleanup fails', async () => {
    let routeHandler, closed = 0;
    const error = new Error('private-player URL, HTML, token and browser call log');
    error.name = 'TimeoutError';
    const page = {setDefaultTimeout() {}, setDefaultNavigationTimeout() {}, on() {},
        goto: async () => {
            await routeHandler(routedRequest('https://site.example/leaderboard'));
            await routeHandler(routedRequest('https://site.example/leaderboard', 'POST'));
            throw error;
        }};
    const browser = {newContext: async () => ({route: async (_, handler) => { routeHandler = handler; },
        newPage: async () => page}), close: async () => { closed++; throw new Error('cleanup secret'); }};
    const report = await check('https://site.example', {chromium: {launch: async () => browser}});
    assert.equal(report.result, 'browser-acceptance-failed');
    assert.equal(report.stage, 'page-contract');
    assert.equal(report.category, 'timeout');
    assert.equal(report.language, 'ru');
    assert.equal(report.scripts, true);
    assert.equal(report.posts, 1);
    assert.equal(report.requests, 2);
    assert.equal(report.requestBoundaryFailed, false);
    assert.equal(closed, 1);
    const serialized = JSON.stringify(report);
    for (const text of ['private-player', 'HTML', 'token', 'call log', 'cleanup secret', 'site.example'])
        assert.ok(!serialized.includes(text));
});


function deferred() {
    let resolve;
    const promise = new Promise(done => { resolve = done; });
    return {promise, resolve};
}

test('RU response cleanup finishes before context closure and EN keeps the shared budget', async () => {
    const guard = createRequestGuard('https://site.example');
    const entered = deferred(), release = deferred();
    let closed = false;
    const route = routedRequest('https://site.example/leaderboard');
    route.fetch = async () => ({status: () => 200, dispose: async () => {
        entered.resolve();
        await release.promise;
        if (closed) throw new Error('Response context is already closed');
    }});
    const routed = guard.route(route);
    await entered.promise;
    guard.verify(); // A rendered response does not mean its handler finished.
    const finishing = closeContext({close: async () => { closed = true; }}, guard);
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(closed, false);
    release.resolve();
    await Promise.all([finishing, routed]);
    assert.equal(closed, true);
    const en = routedRequest('https://site.example/leaderboard?lang=en');
    await guard.route(en);
    guard.verify();
    assert.equal(guard.requests, 2);
    assert.equal(en.calls[0][0], 'fetch');
});

test('real cleanup failure remains fatal and its first phase survives subsequent refusals', async () => {
    const guard = createRequestGuard('https://site.example');
    const route = routedRequest('https://site.example/leaderboard');
    route.fetch = async () => ({status: () => 200, dispose: async () => {
        throw new Error('private URL, name and token');
    }});
    await guard.route(route);
    let closed = false;
    await assert.rejects(closeContext({close: async () => { closed = true; }}, guard));
    assert.equal(closed, false);
    const en = routedRequest('https://site.example/leaderboard?lang=en');
    await guard.route(en);
    assert.deepEqual(en.calls, [['abort']]);
    assert.equal(guard.requests, 1);
    assert.deepEqual(guard.firstFailure, {phase: 'dispose', category: 'check-failed'});
    assert.ok(!JSON.stringify(guard.firstFailure).includes('private'));
});

test('route drain has a deadline and does not swallow a stalled handler', async () => {
    const guard = createRequestGuard('https://site.example', {posts: 2, requests: 3, operationMs: 25});
    const entered = deferred(), release = deferred();
    const route = routedRequest('https://site.example/leaderboard');
    route.fetch = async () => ({status: () => 200, dispose: async () => {
        entered.resolve(); await release.promise;
    }});
    const pending = guard.route(route);
    await entered.promise;
    try {
        await assert.rejects(guard.drain(), /Route drain timed out/);
        assert.deepEqual(guard.firstFailure, {phase: 'drain', category: 'timeout'});
        assert.throws(() => guard.verify());
    } finally {
        release.resolve(); await pending;
    }
});


test('a handler failure during context closure cannot mark the scenario complete', async () => {
    const guard = createRequestGuard('https://site.example');
    let closed = false;
    const route = routedRequest('https://site.example/leaderboard');
    route.fulfill = async () => { throw new Error('Context interrupted fulfillment'); };
    await assert.rejects(closeContext({close: async () => {
        closed = true;
        await guard.route(route);
    }}, guard), /Request boundary failed/);
    assert.equal(closed, true);
    assert.deepEqual(guard.firstFailure, {phase: 'fulfill', category: 'check-failed'});
});
