'use strict';

const path = require('node:path');
const { randomBytes } = require('node:crypto');
const { optionsFrom, prepareOutput, createContext, preflight } = require('./native-acceptance/runner.cjs');
const { writeReport } = require('./native-acceptance/report-writer.cjs');
const { runGroupScenarios } = require('./native-acceptance/group-scenarios.cjs');

const REQUIRED = ['create_group', 'delete_group', 'rename', 'write_blocks', 'delete_block', 'get_block', 'list_blocks', 'list_udts', 'list_tag_tables', 'list_technology_objects'];
const now = () => new Date().toISOString();

async function runGroupAcceptance(options, dependencies = {}) {
  const prefix = 'McpGrp_' + randomBytes(4).toString('hex');
  const output = path.resolve(options.output || path.join(__dirname, '../test-results/native-group-acceptance', now().replace(/[:.]/g, '-') + '_' + prefix));
  prepareOutput(output);
  const report = { schemaVersion: 1, suite: 'native-group', status: 'running', startedAtUtc: now(), endpoint: options.endpoint,
    processId: options.processId, projectPath: options.projectPath, prefix, fixture: { owned: [] }, steps: [], calls: [],
    observedAffectedObjects: [], writesAttempted: false, uncertainWrite: false };
  let ctx;
  const persist = () => {
    report.fixture.owned = ctx?.owned || [];
    writeReport(output, report, () => '# Native group acceptance\n\n' +
      `Result: **${report.status}**\n\nPrefix: ${report.prefix}\n\n` +
      report.steps.map(step => `- ${step.id}: ${step.status}${step.error ? ' — ' + step.error : ''}`).join('\n') + '\n\n' +
      'Recorded fixture ownership:\n\n```json\n' + JSON.stringify(report.fixture.owned, null, 2) + '\n```\n\n' +
      (report.fixture.cleanupErrors?.length ? 'Fixture cleanup:\n\n' + report.fixture.cleanupErrors.map(error => '- ' + error).join('\n') + '\n\n' : '') +
      (report.uncertainWrite ? 'A write outcome is uncertain. Fixture cleanup stopped; inspect the project and recorded response before any further writes.\n' : '') +
      'Requests and raw responses are in report.json. Passed scenarios verify their stated engineering readback, not PLC runtime behavior.\n');
  };
  ctx = createContext({ ...options,
    requiredTools: ['list_tia_processes', 'get_status', 'list_devices', 'get_device', ...REQUIRED],
    allocateAddress: false, onProgress: dependencies.onProgress }, report, persist, dependencies.fetchImpl);
  ctx.owned = [];
  try {
    await preflight(ctx, report);
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
