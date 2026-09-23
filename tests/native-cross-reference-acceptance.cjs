'use strict';
const { optionsFrom }=require('./native-acceptance/runner.cjs');
const { run }=require('./native-acceptance/cross-reference-scenarios.cjs');
async function main(){
 const options=optionsFrom(process.argv.slice(2));
 if(options.help){console.log('node tests/native-cross-reference-acceptance.cjs --process-id <PID> --project-path <absolute .ap20 path>');console.log('Requires a manually connected disposable V20 project. Creates, edits, compiles and deletes its own fixtures; no save or PLC transfer. Supports the common endpoint, CPU, address, output and timeout options; no resume.');return;}
 const {report,output}=await run(options);console.log(JSON.stringify({status:report.status,error:report.error,steps:report.steps.length,output},null,2));process.exitCode=report.status==='passed'?0:1;
}
if(require.main===module)main().catch(error=>{console.error(error);process.exitCode=1;});
