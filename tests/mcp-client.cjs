'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { randomBytes } = require('node:crypto');
const { TOOLS, WRITES, PROJECT_PATH, canonical } = require('./mcp-fixture.cjs');

function optionsFrom(argv) {
  const values = {};
  const allowed = new Set(['process-id', 'endpoint', 'timeout-ms']);
  for (let index = 0; index < argv.length; index++) {
    if (argv[index] === '--help' && argv.length === 1) return { help: true };
    const key = argv[index].replace(/^--/, '');
    assert.ok(argv[index].startsWith('--') && allowed.has(key) && !Object.hasOwn(values, key) &&
      argv[index + 1] && !argv[index + 1].startsWith('--'), 'Use unique --process-id, --endpoint and --timeout-ms arguments.');
    values[key] = argv[++index];
  }
  const processId = Number(values['process-id']);
  assert.ok(Number.isInteger(processId) && processId > 0 && processId <= 2147483647, '--process-id is required.');
  const endpoint = values.endpoint || 'http://127.0.0.1:5000/mcp';
  const url = new URL(endpoint);
  assert.ok(url.protocol === 'http:' && ['127.0.0.1', 'localhost'].includes(url.hostname) &&
    url.pathname === '/mcp' && !url.username && !url.password && !url.search && !url.hash,
  'The endpoint must be the existing local HTTP /mcp server.');
  const timeoutMs = Number(values['timeout-ms'] || 120000);
  assert.ok(Number.isInteger(timeoutMs) && timeoutMs >= 1000 && timeoutMs <= 600000, '--timeout-ms must be 1000..600000.');
  return { processId, endpoint, timeoutMs };
}

class LiveClient {
  constructor(options) {
    this.options = options;
    this.prefix = 'McpLive_' + randomBytes(6).toString('hex');
    this.output = path.resolve(__dirname, '../test-results/mcp-live', new Date().toISOString().replace(/[:.]/g, '-') + '_' + this.prefix);
    fs.mkdirSync(this.output, { recursive: true });
    // Keep one report handle open. Replacing the filename can fail on Windows
    // when a filesystem watcher briefly opens the previous report.
    this.reportHandle = fs.openSync(path.join(this.output, 'report.json'), 'w');
    this.report = { schemaVersion: 1, status: 'running', startedAtUtc: new Date().toISOString(),
      endpoint: options.endpoint, processId: options.processId, projectPath: PROJECT_PATH, prefix: this.prefix,
      coverage: Object.fromEntries(TOOLS.map(name => [name, { status: 'pending', checks: [] }])),
      fixture: { groups: {}, objects: {} }, stages: [], calls: [], uncertainWrite: false };
    this.stopped = false;
    this.persistenceFailed = false;
    this.persist();
  }

  persist() {
    try {
      const bytes = Buffer.from(JSON.stringify(this.report, null, 2), 'utf8');
      let offset = 0;
      while (offset < bytes.length) {
        const written = fs.writeSync(this.reportHandle, bytes, offset, bytes.length - offset, offset);
        if (written === 0) throw new Error('Report write made no progress.');
        offset += written;
      }
      fs.ftruncateSync(this.reportHandle, bytes.length);
      fs.fsyncSync(this.reportHandle);
    } catch (error) {
      this.persistenceFailed = true;
      this.stopped = true;
      throw new Error('Evidence persistence failed; no further requests are allowed: ' + error.message);
    }
  }

  assertTarget(status, bind = false) {
    assert.equal(status.processId, this.options.processId);
    assert.equal(status.state, 'connected', 'Manually connect Demo in the dashboard.');
    assert.equal(status.writeToolsAvailable, true, 'Use the full-access server.');
    assert.equal(canonical(status.project?.path), canonical(PROJECT_PATH), 'Only repository tia/Demo/Demo.ap20 is permitted.');
    // Runtime/native project identity is guarded by the production ticket on each
    // call; public MCP status does not expose an attachment identity across calls.
    if (bind) this.report.target = { processId: status.processId, projectPath: status.project.path };
  }

  async rpc(method, params) {
    assert.equal(this.stopped, false, 'The run stopped; requests cannot resume.');
    const trace = { number: this.report.calls.length + 1, stage: this.currentStage,
      startedAtUtc: new Date().toISOString(), transmissionState: 'prepared',
      request: { jsonrpc: '2.0', id: this.report.calls.length + 1, method, params } };
    this.report.calls.push(trace);
    // Persist intent before fetch. A crash while prepared leaves an uncertain intent.
    this.persist();
    const started = performance.now();
    try {
      trace.transmissionState = 'started';
      const response = await fetch(this.options.endpoint, { method: 'POST', redirect: 'error',
        headers: { 'Content-Type': 'application/json', Accept: 'application/json, text/event-stream' },
        body: JSON.stringify(trace.request), signal: AbortSignal.timeout(this.options.timeoutMs) });
      trace.httpStatus = response.status;
      trace.responseText = await response.text();
      trace.transmissionState = 'received';
      assert.equal(response.status, 200, `MCP HTTP ${response.status}.`);
      const envelope = JSON.parse(trace.responseText);
      assert.equal(envelope.jsonrpc, '2.0');
      assert.equal(envelope.id, trace.request.id, 'MCP response identity mismatch.');
      assert.ok(!envelope.error, 'JSON-RPC error: ' + JSON.stringify(envelope.error));
      assert.ok(envelope.result && typeof envelope.result === 'object');
      trace.response = envelope;
      return envelope.result;
    } catch (error) {
      trace.error = error.message;
      this.stopped = true;
      if (WRITES.includes(params?.name)) this.report.uncertainWrite = true;
      throw error;
    } finally {
      trace.finishedAtUtc = new Date().toISOString();
      trace.durationMs = performance.now() - started;
      try { this.persist(); }
      catch (error) {
        if (WRITES.includes(params?.name)) this.report.uncertainWrite = true;
        throw error;
      }
    }
  }

  async call(name, args = {}, { expectedError = false } = {}) {
    assert.ok(TOOLS.includes(name), 'Unknown fixture tool: ' + name);
    if (Object.hasOwn(args, 'processId')) assert.equal(args.processId, this.options.processId);
    if (name !== 'list_tia_processes' && name !== 'get_status') assert.equal(args.processId, this.options.processId);
    if (WRITES.includes(name)) {
      assert.ok(this.report.target, 'Bind the target before writing.');
      this.assertTarget(await this.call('get_status', { processId: this.options.processId }));
      this.report.writesAttempted = true;
      this.persist();
    }
    const result = await this.rpc('tools/call', { name, arguments: args });
    const trace = this.report.calls[this.report.calls.length - 1];
    try {
      assert.ok(Array.isArray(result.content));
      const text = result.content.find(item => item.type === 'text')?.text;
      assert.equal(typeof text, 'string');
      const payload = JSON.parse(text);
      trace.payload = payload;
      assert.equal(result.isError, expectedError, `${name} isError mismatch: ${text}`);
      if (Object.hasOwn(args, 'processId')) assert.equal(payload.processId, args.processId);
      assert.ok(Number.isFinite(Date.parse(payload.readAtUtc)), `${name} needs readAtUtc.`);
      assert.ok(Array.isArray(payload.errors), `${name} needs errors.`);
      if (!expectedError) {
        assert.deepEqual(payload.errors, [], `${name} returned errors.`);
        if (name !== 'list_tia_processes' && !(name === 'get_status' && !Object.hasOwn(args, 'processId')))
          assert.equal(payload.complete, true, `${name} returned incomplete output.`);
      }
      if (WRITES.includes(name)) {
        assert.equal(payload.operation, name);
        assert.equal(payload.saved, false);
        if (name !== 'compile_plc') {
          assert.equal(payload.cleanupFailed, false);
          assert.ok(Array.isArray(payload.affectedObjects));
          trace.affectedObjects = payload.affectedObjects;
        }
      }
      this.persist();
      return payload;
    } catch (error) {
      trace.assertionError = error.message;
      this.stopped = true;
      if (WRITES.includes(name) && !trace.payload) this.report.uncertainWrite = true;
      this.persist();
      throw error;
    }
  }

  async stage(id, description, coverage, action) {
    const stage = { id, description, status: 'running', startedAtUtc: new Date().toISOString(), firstCall: this.report.calls.length + 1 };
    this.report.stages.push(stage);
    this.currentStage = id;
    this.persist();
    try {
      stage.details = await action();
      for (const name of coverage) {
        const calls = this.report.calls.filter(call => call.stage === id && call.request.params?.name === name).map(call => call.number);
        assert.ok(calls.length > 0, 'A coverage assertion must invoke ' + name + ' in its stage.');
        this.report.coverage[name].status = 'passed';
        this.report.coverage[name].checks.push({ stage: id, description, calls });
      }
      stage.status = 'passed';
      console.log('PASS ' + id);
    } catch (error) {
      stage.status = 'failed';
      stage.error = error.message;
      this.stopped = true;
      throw error;
    } finally {
      stage.finishedAtUtc = new Date().toISOString();
      this.currentStage = null;
      this.persist();
    }
  }

  finish(error) {
    const fixture = this.report.fixture;
    this.report.remainingFixtures = [
      ...Object.entries(fixture.objects).map(([key, value]) => ({ key, ...value })),
      ...Object.entries(fixture.groups).flatMap(([kind, levels]) =>
        Object.entries(levels).map(([level, value]) => ({ key: kind + '.' + level, ...value })))
    ].filter(item => item.deleted === false);
    this.report.unverifiedTools = TOOLS.filter(name => this.report.coverage[name].status !== 'passed');
    this.report.status = error || this.report.unverifiedTools.length ? 'failed' : 'passed';
    this.report.error = error?.message;
    this.report.finishedAtUtc = new Date().toISOString();
    this.stopped = true;
    if (!this.persistenceFailed) this.persist();
    fs.closeSync(this.reportHandle);
    return this.report;
  }
}

module.exports = { optionsFrom, LiveClient };
