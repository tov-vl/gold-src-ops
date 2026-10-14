const {test} = require('node:test');
const assert = require('node:assert/strict');
const {hasNoStore, unexpectedFailures, refresh} = require('../release/check-player-search-browser.cjs');

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
