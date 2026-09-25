'use strict';

const assert = require('node:assert/strict');
const path = require('node:path');
const { randomBytes } = require('node:crypto');
const { optionsFrom, prepareOutput, targetStatus } = require('./native-acceptance/runner.cjs');
const { writeReport } = require('./native-acceptance/report-writer.cjs');
const { runGroupScenarios } = require('./native-acceptance/group-scenarios.cjs');

const REQUIRED = ['create_group', 'delete_group', 'rename', 'write_blocks', 'delete_block', 'get_block', 'list_blocks', 'list_udts', 'list_tag_tables', 'list_technology_objects'];
const now = () => new Date().toISOString();

function canonical(value) { return path.win32.normalize(value).toLowerCase(); }

async function runGroupAcceptance(options, dependencies = {}) {
  const prefix = 'McpGrp_' + randomBytes(4).toString('hex');
  const output = path.resolve(options.output || path.join(__dirname, '../test-results/native-group-acceptance', now().replace(/[:.]/g, '-') + '_' + prefix));
  prepareOutput(output);
  const report = { schemaVersion: 1, suite: 'native-group', status: 'running', startedAtUtc: now(), endpoint: options.endpoint,
    processId: options.processId, projectPath: options.projectPath, prefix, fixture: { groups: [] }, steps: [], calls: [] };
  const persist = () => writeReport(output, report, () => '# Native group acceptance\n\n' +
    `Result: **${report.status}**\n\nPrefix: ${report.prefix}\n\n` +
    report.steps.map(step => `- ${step.id}: ${step.status}${step.error ? ' — ' + step.error : ''}`).join('\n') + '\n');
  const fetchImpl = dependencies.fetchImpl || fetch;
  let nextId = 0;
  const ctx = { processId: options.processId, plcObjectId: options.plcObjectId, prefix, owned: [] };
  ctx.rpc = async (method, params) => {
    const request = { jsonrpc: '2.0', id: ++nextId, method, params };
    const trace = { number: nextId, startedAtUtc: now(), request };
    report.calls.push(trace);
    persist();
    const response = await fetchImpl(options.endpoint, { method: 'POST', redirect: 'error',
      headers: { 'Content-Type': 'application/json', Accept: 'application/json' },
      body: JSON.stringify(request), signal: AbortSignal.timeout(options.timeoutMs) });
    trace.httpStatus = response.status;
    trace.responseText = await response.text();
    trace.finishedAtUtc = now();
    persist();
    assert.equal(response.status, 200, trace.responseText);
    const envelope = JSON.parse(trace.responseText);
    assert.equal(envelope.id, request.id);
    assert.ok(!envelope.error, JSON.stringify(envelope.error));
    return envelope.result;
  };
  ctx.rawCall = async (name, args) => {
    const result = await ctx.rpc('tools/call', { name, arguments: args });
    const text = result.content.find(item => item.type === 'text')?.text;
    assert.equal(typeof text, 'string', name);
    return { result, payload: JSON.parse(text) };
  };
  ctx.call = async (name, args) => {
    const response = await ctx.rawCall(name, args);
    assert.equal(response.result.isError, false, `${name}: ${JSON.stringify(response.payload.error || response.payload.errors)}`);
    assert.equal(response.payload.complete, true, name);
    assert.equal(response.payload.errors?.length || 0, 0, name);
    return response.payload;
  };
  ctx.step = async (id, description, action) => {
    const step = { id, description, status: 'running', startedAtUtc: now() };
    report.steps.push(step);
    persist();
    try { step.details = await action(); step.status = 'passed'; }
    catch (error) { step.status = 'failed'; step.error = error.message; throw error; }
    finally { step.finishedAtUtc = now(); persist(); }
  };
  try {
    await ctx.step('preflight', 'Verify the group tools are published and the selected project is connected.', async () => {
      const listing = await ctx.rpc('tools/list', {});
      const names = listing.tools.map(tool => tool.name);
      for (const name of REQUIRED) assert.ok(names.includes(name), 'Missing ' + name);
      const status = await ctx.call('get_status', { processId: ctx.processId });
      targetStatus(status, options);
      if (!ctx.plcObjectId) {
        const devices = await ctx.call('list_devices', { processId: ctx.processId });
        const cpus = [];
        const visit = nodes => { for (const node of nodes || []) { if (node.plcObjectId) cpus.push(node.plcObjectId); visit(node.deviceItems || node.children); } };
        for (const device of devices.roots || []) {
          if (device.kind !== 'device' || !device.objectId) continue;
          const details = await ctx.call('get_device', { processId: ctx.processId, objectId: device.objectId, includePath: false });
          visit(details.deviceItems);
        }
        const unique = [...new Set(cpus)];
        assert.equal(unique.length, 1, 'Select one CPU with --plc-object-id.');
        ctx.plcObjectId = unique[0];
      }
      report.plcObjectId = ctx.plcObjectId;
      assert.equal(canonical(status.project.path), canonical(options.projectPath));
    });
    await runGroupScenarios(ctx);
    report.status = 'passed';
  } catch (error) { report.status = 'failed'; report.error = error.message; }
  finally { report.finishedAtUtc = now(); persist(); }
  return { report, output };
}

async function main() {
  const options = optionsFrom(process.argv.slice(2));
  if (options.help) {
    console.log('Native group create, rename and delete acceptance through the existing MCP server.');
    console.log('Manually connect a disposable TIA project first.');
    console.log('node tests/native-group-acceptance.cjs --process-id <PID> --project-path <absolute .ap20 path>');
    console.log('Creates uniquely named groups and one SCL function, checks rename, then deletes those fixtures.');
    console.log('Never connects, saves, compiles, uploads, downloads, closes or retries.');
    return;
  }
  const { report, output } = await runGroupAcceptance(options);
  console.log(`${report.status.toUpperCase()}: ${report.steps.filter(step => step.status === 'passed').length}/${report.steps.length} scenarios passed.`);
  if (report.error) console.error(report.error);
  console.log(`Evidence: ${output}`);
  process.exitCode = report.status === 'passed' ? 0 : 1;
}

if (require.main === module) main().catch(error => { console.error(error.message); process.exitCode = 1; });

module.exports = { runGroupAcceptance };
