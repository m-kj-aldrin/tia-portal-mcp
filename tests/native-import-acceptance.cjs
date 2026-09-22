'use strict';

const { optionsFrom } = require('./native-acceptance/runner.cjs');
const { runImportMatrix } = require('./native-acceptance/import-matrix.cjs');

async function main() {
  const options = optionsFrom(process.argv.slice(2));
  if (options.help) {
    console.log('Native source-import matrix through the existing MCP server. Manually connect a disposable project first.');
    console.log('node tests/native-import-acceptance.cjs --process-id <PID> --project-path <absolute .ap20 path>');
    console.log('Options: --plc-object-id <CPU ID> --address %M0.0 --endpoint http://127.0.0.1:5000/mcp --output <new directory> --timeout-ms 120000');
    console.log('If the report requests an offline compile, compile manually and add --resume-report <blocked report.json>. Verified writes are never repeated.');
    console.log('A diagnosed readback-only failure may also be explicitly resumed with unchanged fixture documents. Failed writes cannot be resumed.');
    console.log('Creates and edits unique test objects. Never connects, saves, compiles, downloads, closes or retries a failed write.');
    return;
  }
  const { report, output } = await runImportMatrix(options);
  console.log(`${report.status.toUpperCase()}: ${report.matrix.filter(row => row.create.status === 'passed' && row.update.status === 'passed').length}/${report.matrix.length} import cases verified for creation and semantic update.`);
  if (report.error) console.error(report.error);
  console.log(`Evidence: ${output}`);
  process.exitCode = report.status === 'passed' ? 0 : 1;
}

main().catch(error => { console.error(error.message); process.exitCode = 1; });
