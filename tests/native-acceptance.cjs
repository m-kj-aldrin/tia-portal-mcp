'use strict';

const { optionsFrom, run } = require('./native-acceptance/runner.cjs');

async function main() {
  const options = optionsFrom(process.argv.slice(2));
  if (options.help) {
    console.log('Native acceptance against the one running MCP server. You must manually connect a disposable TIA project first.');
    console.log('node tests/native-acceptance.cjs --process-id <PID> --project-path <absolute .ap20 path>');
    console.log('Optional: --plc-object-id <CPU ID> --address %M0.0 --endpoint http://127.0.0.1:5000/mcp --output <report directory> --timeout-ms 120000');
    console.log('After the user compiles the PLC software offline: add --resume-report <stopped report.json> to continue the existing fixtures.');
    console.log('This command WRITES unique test objects, performs readback, and leaves block/UDT/table fixtures for inspection. It never connects, saves, compiles, closes or retries.');
    return;
  }
  const { report, output } = await run(options);
  console.log(`${report.status.toUpperCase()}: ${report.steps.filter(step => step.status === 'passed').length}/${report.steps.length} completed scenarios.`);
  if (report.error) console.error(report.error);
  console.log(`Evidence: ${output}`);
  process.exitCode = report.status === 'passed' ? 0 : 1;
}

main().catch(error => { console.error(error.message); process.exitCode = 1; });
