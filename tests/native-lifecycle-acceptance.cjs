'use strict';

const { optionsFrom } = require('./native-acceptance/runner.cjs');
const { runLifecycleAcceptance } = require('./native-acceptance/lifecycle-scenarios.cjs');

async function main() {
  const options = optionsFrom(process.argv.slice(2));
  if (options.help) {
    console.log('Native compile, deletion and tag-table XML export acceptance through the existing MCP server.');
    console.log('Manually connect a disposable TIA project first. Keep the PLC offline.');
    console.log('node tests/native-lifecycle-acceptance.cjs --process-id <PID> --project-path <absolute .ap20 path>');
    console.log('Options: --plc-object-id <CPU ID> --address %M0.0 --endpoint http://127.0.0.1:5000/mcp --output <new directory> --timeout-ms 120000');
    console.log('Creates owned fixtures, exports/reimports a populated tag table, compiles success/error/repair cases, then deletes its fixtures and compiles again.');
    console.log('Never connects, saves, uploads, downloads, closes, retries or resumes failed writes. A failed run may leave its recorded fixtures for inspection.');
    return;
  }
  const { report, output } = await runLifecycleAcceptance(options);
  console.log(`${report.status.toUpperCase()}: ${report.steps.filter(step => step.status === 'passed').length}/${report.steps.length} scenarios passed.`);
  if (report.error) console.error(report.error);
  console.log(`Evidence: ${output}`);
  process.exitCode = report.status === 'passed' ? 0 : 1;
}

if (require.main === module) main().catch(error => { console.error(error.message); process.exitCode = 1; });

module.exports = { main };
