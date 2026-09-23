'use strict';
const assert = require('node:assert/strict');
const path = require('node:path');
const { randomBytes } = require('node:crypto');
const { createContext, preflight, prepareOutput, targetStatus } = require('./runner.cjs');
const { writeReport } = require('./report-writer.cjs');
const { assertCompilation } = require('./lifecycle-scenarios.cjs');
const walk = nodes => nodes.flatMap(node => [node, ...walk(node.children || [])]);

function fixtures(prefix) {
  assert.match(prefix, /^McpXR_[a-f0-9]{12}$/);
  const names = Object.fromEntries(['Tags','Signal','Unused','Limit','Type','DB','Reader','Writer','Caller','Top','LocalOnly'].map(key => [key, prefix + '_' + key]));
  const fc = (name, declarations, body) => 'FUNCTION "'+name+'" : Void\nVERSION : 0.1\n'+declarations+'BEGIN\n'+body+'END_FUNCTION\n';
  const specs = [
    { key:'Type',kind:'udt',extension:'.udt', content:'TYPE "'+names.Type+'"\nVERSION : 0.1\n STRUCT\n Flag : Bool;\n Count : Int;\n END_STRUCT;\nEND_TYPE\n' },
    { key:'DB',kind:'block',extension:'.db', content:'DATA_BLOCK "'+names.DB+'"\n{ S7_Optimized_Access := \'TRUE\' }\nVERSION : 0.1\nNON_RETAIN\n VAR\n Data : "'+names.Type+'";\n END_VAR\nBEGIN\nEND_DATA_BLOCK\n' },
    { key:'Reader',kind:'block',extension:'.scl', content:fc(names.Reader,' VAR_OUTPUT\n First : Bool;\n Second : Bool;\n Count : Int;\n END_VAR\n',
      ' #First := "'+names.Signal+'";\n #Second := "'+names.Signal+'";\n #Count := "'+names.DB+'".Data.Count + "'+names.Limit+'";\n') },
    { key:'Writer',kind:'block',extension:'.scl', content:fc(names.Writer,'',
      ' "'+names.Signal+'" := TRUE;\n "'+names.Signal+'" := FALSE;\n "'+names.DB+'".Data.Flag := "'+names.Signal+'";\n "'+names.DB+'".Data.Count := "'+names.DB+'".Data.Count + 1;\n'),
      updated:fc(names.Writer,'',' "'+names.DB+'".Data.Flag := FALSE;\n "'+names.DB+'".Data.Count := "'+names.DB+'".Data.Count + 1;\n') },
    { key:'Caller',kind:'block',extension:'.scl', content:fc(names.Caller,' VAR_TEMP\n One : Bool;\n Two : Bool;\n Count : Int;\n END_VAR\n',
      ' "'+names.Reader+'"(First => #One, Second => #Two, Count => #Count);\n "'+names.Writer+'"();\n') },
    { key:'Top',kind:'block',extension:'.scl', content:fc(names.Top,'',' "'+names.Caller+'"();\n') },
    { key:'LocalOnly',kind:'block',extension:'.scl', content:fc(names.LocalOnly,' VAR_TEMP\n "'+names.Signal+'" : Bool;\n Copy : Bool;\n END_VAR\n',
      ' #"'+names.Signal+'" := TRUE;\n #Copy := #"'+names.Signal+'";\n') }
  ].map(spec => ({...spec,name:names[spec.key]}));
  return { names, specs };
}

function locations(result) {
  assert.equal(result.complete,true); assert.deepEqual(result.errors,[]);
  assert.ok(Array.isArray(result.sources));
  const rows=[];
  function visit(nodes, ancestry) {
    for(const source of nodes) {
      assert.ok(Array.isArray(source.children)); assert.ok(Array.isArray(source.references));
      for(const reference of source.references) {
        assert.ok(Array.isArray(reference.locations));
        for(const location of reference.locations) rows.push({source, ancestry, reference, location});
      }
      visit(source.children,[...ancestry,source]);
    }
  }
  visit(result.sources,[]); return rows;
}
function relation(result, targetId, direction, access, minimum=1) {
  const found=locations(result).filter(row=>row.reference.objectId===targetId &&
    (!direction || row.location.referenceType===direction) &&
    (!access || String(row.location.access).split(',').map(x=>x.trim()).includes(access)));
  assert.ok(found.length>=minimum,'Missing '+direction+'/'+access+' relationship to '+targetId+' (expected '+minimum+' locations).');
  for(const row of found) assert.ok(typeof row.location.referenceLocation==='string' && row.location.referenceLocation.trim(),'Native location must be reported.');
  // Native SCL locations can share the same program-code label. Preserve every
  // occurrence; uniqueness of that display text is not a native guarantee.
  return found.map(row=>({source:row.source.name, sourceId:row.source.objectId, target:row.reference.name, targetId:row.reference.objectId,
    referenceType:row.location.referenceType,access:row.location.access,location:row.location.referenceLocation}));
}
function absent(result,id,name) {
  assert.ok(!locations(result).some(row=>row.reference.objectId===id || row.reference.name===name),'Removed or unrelated object still appears as a reference.');
}
async function inventory(ctx,kind) {
  const tool=kind==='udt'?'list_udts':kind==='tagTable'?'list_tag_tables':'list_blocks';
  const result=await ctx.call(tool,{processId:ctx.processId,plcObjectId:ctx.plcObjectId});
  assert.equal(result.plcObjectId,ctx.plcObjectId);assert.ok(Array.isArray(result.roots));return walk(result.roots).filter(node=>node.kind===kind);
}
async function owned(ctx,key) {
  const object=ctx.fixture.objects[key]; assert.ok(object);
  const found=(await inventory(ctx,object.kind)).filter(node=>node.name.toLowerCase()===object.name.toLowerCase());
  assert.equal(found.length,1); assert.equal(found[0].objectId,object.objectId,'A same-name replacement cannot be adopted.');return object;
}
function affected(result,kind,name) {
  assert.equal(result.affectedObjects.length,1);const object=result.affectedObjects[0];
  assert.equal(object.kind,kind);assert.equal(object.name,name);assert.ok(object.objectId);return {...object};
}
async function writeSource(ctx,spec,update=false) {
  if(update) await owned(ctx,spec.key);
  else assert.ok(!(await inventory(ctx,spec.kind)).some(node=>node.name.toLowerCase()===spec.name.toLowerCase()));
  const result=await ctx.call(spec.kind==='udt'?'write_udts':'write_blocks',{processId:ctx.processId,plcObjectId:ctx.plcObjectId,
    sourceFormat:'external-source',documents:[{name:spec.name+spec.extension,content:update?spec.updated:spec.content}]});
  ctx.fixture.objects[spec.key]=affected(result,spec.kind,spec.name);
  const object=await owned(ctx,spec.key);
  const read=await ctx.call(spec.kind==='udt'?'get_udt':'get_block',{processId:ctx.processId,objectId:object.objectId,includeSource:true,sourceFormat:'external-source',includePath:false});
  assert.equal(read.metadata.name,spec.name);assert.equal(read.source.format,'external-source');
  assert.ok(read.source.documents.some(d=>d.content.includes(spec.name)));
  return {objectId:object.objectId,name:object.name};
}
async function compile(ctx) { return assertCompilation(await ctx.rawCall('compile_plc',{processId:ctx.processId,plcObjectId:ctx.plcObjectId}),ctx,true); }
async function query(ctx,key) {
  const object=ctx.fixture.objects[key];
  const result=await ctx.call('get_cross_references',{processId:ctx.processId,objectId:object.objectId});
  if(result.sources.length) assert.ok(walk(result.sources).some(source=>source.objectId===object.objectId),'A nonempty result must preserve the selected native source identity.');
  locations(result);return result;
}
async function deleteOwned(ctx,key) {
  const object=await owned(ctx,key);
  const suffix=object.kind==='udt'?'udt':object.kind==='tagTable'?'tag_table':'block';
  const result=await ctx.call('delete_'+suffix,{processId:ctx.processId,objectId:object.objectId});
  assert.equal(affected(result,object.kind,object.name).objectId,object.objectId);
  assert.ok(!(await inventory(ctx,object.kind)).some(node=>node.objectId===object.objectId||node.name===object.name));
  const missing=await ctx.rawCall('get_'+suffix,{processId:ctx.processId,objectId:object.objectId,includePath:false,
    ...(object.kind==='tagTable'?{includeEntries:false}:{includeSource:false})});
  assert.equal(missing.result.isError,true);assert.equal(missing.payload.error.code,'objectNotFound');
  object.deleted=true;
}
async function runScenarios(ctx) {
  const {names,specs}=fixtures(ctx.prefix);ctx.fixture.objects={};ctx.fixture.names=names;
  await ctx.step('fixture.tags','Create two separate Bool tags and one Int constant; verify their own native identities.',async()=>{
    assert.ok(!(await inventory(ctx,'tagTable')).some(node=>node.name===names.Tags));
    const table=affected(await ctx.call('create_tag_table',{processId:ctx.processId,plcObjectId:ctx.plcObjectId,name:names.Tags}),'tagTable',names.Tags);
    ctx.fixture.objects.Tags=table;
    const match=/^(%M\d+)\.([0-7])$/.exec(ctx.logicalAddress);assert.ok(match);
    const unused=match[1]+'.'+((Number(match[2])+1)%8);
    for(const [key,address] of [['Signal',ctx.logicalAddress],['Unused',unused]]) {
      await owned(ctx,'Tags');ctx.fixture.objects[key]=affected(await ctx.call('create_tag',{processId:ctx.processId,objectId:table.objectId,name:names[key],dataType:'Bool',logicalAddress:address}),'tag',names[key]);
    }
    await owned(ctx,'Tags');ctx.fixture.objects.Limit=affected(await ctx.call('create_user_constant',{processId:ctx.processId,objectId:table.objectId,name:names.Limit,dataType:'Int',value:'7'}),'userConstant',names.Limit);
    const read=await ctx.call('get_tag_table',{processId:ctx.processId,objectId:table.objectId,includeEntries:true});
    assert.equal(read.entries.tags.length,2);assert.equal(read.entries.userConstants.length,1);
    for(const key of ['Signal','Unused','Limit']) assert.ok([...read.entries.tags,...read.entries.userConstants].some(item=>item.objectId===ctx.fixture.objects[key].objectId && item.name===names[key]));
  });
  for(const spec of specs) await ctx.step('fixture.'+spec.key,'Create and read back '+spec.name,()=>writeSource(ctx,spec));
  await ctx.step('compile.baseline','Compile the complete fixture before interpreting references.',()=>compile(ctx));
  await runReferenceScenarios(ctx);
}
async function runReferenceScenarios(ctx) {
  const {names,specs}=fixtures(ctx.prefix);
  const id=key=>ctx.fixture.objects[key].objectId;
  await ctx.step('references.tag','Verify two reader locations, two writer locations and no local-name false match.',async()=>{
    const result=await query(ctx,'Signal');
    const proof=[...relation(result,id('Reader'),'UsedBy','Read',2),...relation(result,id('Writer'),'UsedBy','Write',2),...relation(result,id('Writer'),'UsedBy','Read')];
    absent(result,id('LocalOnly'),names.LocalOnly);return proof;
  });
  await ctx.step('references.unused','Verify the unused tag has no UsedBy locations.',async()=>{
    const result=await query(ctx,'Unused');assert.ok(!locations(result).some(row=>row.location.referenceType==='UsedBy'));return {sources:result.sources.length};
  });
  await ctx.step('references.constant','Verify native references from the user constant to its reader.',async()=>relation(await query(ctx,'Limit'),id('Reader'),'UsedBy','Read'));
  await ctx.step('references.reader-uses','Verify outgoing tag, constant and DB reads from the Reader.',async()=>{
    const result=await query(ctx,'Reader');return [...relation(result,id('Signal'),'Uses','Read',2),...relation(result,id('Limit'),'Uses','Read'),...relation(result,id('DB'),'Uses','Read')];
  });
  await ctx.step('references.writer-uses','Verify outgoing tag and DB reads/writes from the Writer.',async()=>{
    const result=await query(ctx,'Writer');return [...relation(result,id('Signal'),'Uses','Read'),...relation(result,id('Signal'),'Uses','Write',2),...relation(result,id('DB'),'Uses','Read'),...relation(result,id('DB'),'Uses','Write',2)];
  });
  await ctx.step('references.caller-uses','Verify outgoing calls to both FCs.',async()=>{
    const result=await query(ctx,'Caller');return [...relation(result,id('Reader'),'Uses','Call'),...relation(result,id('Writer'),'Uses','Call')];
  });
  for(const [callee,caller] of [['Reader','Caller'],['Writer','Caller'],['Caller','Top']])
    await ctx.step('references.call.'+callee,'Verify native call relationship '+caller+' to '+callee,async()=>relation(await query(ctx,callee),id(caller),'UsedBy','Call'));
  await ctx.step('references.db','Verify DB member hierarchy and reads/writes from different FCs.',async()=>{
    const result=await query(ctx,'DB');const children=walk(result.sources).filter(source=>!result.sources.includes(source));
    assert.ok(children.length>0,'DB members must remain represented in native child sources.');
    const data=children.find(source=>source.name==='"'+names.DB+'".Data');assert.ok(data,'The UDT instance member must be present.');
    const flag=data.children.find(source=>source.name==='"'+names.DB+'".Data.Flag');
    const count=data.children.find(source=>source.name==='"'+names.DB+'".Data.Count');assert.ok(flag&&count,'Both nested UDT members must be preserved.');
    const member=source=>({complete:true,errors:[],sources:[source]});
    relation(member(flag),id('Writer'),'UsedBy','Write');
    relation(member(count),id('Reader'),'UsedBy','Read');relation(member(count),id('Writer'),'UsedBy','Read');relation(member(count),id('Writer'),'UsedBy','Write');
    relation(member(data),id('Type'),'InstanceType','Declaration');
    const proof=[...relation(result,id('Reader'),'UsedBy','Read'),...relation(result,id('Writer'),'UsedBy','Write')];
    return {children:children.map(source=>({name:source.name,objectId:source.objectId,path:source.path})),relationships:proof};
  });
  await ctx.step('references.udt','Verify the UDT-to-DB TypeInstance/Declaration dependency.',async()=>relation(await query(ctx,'Type'),id('DB'),'TypeInstance','Declaration'));
  await ctx.step('edit.remove-tag-uses','Replace the writer source to remove its global-tag references.',()=>writeSource(ctx,specs.find(spec=>spec.key==='Writer'),true));
  await ctx.step('compile.edited','Compile the changed fixture.',()=>compile(ctx));
  await ctx.step('references.after-edit','Verify removed writer references disappear while reader locations remain.',async()=>{
    const result=await query(ctx,'Signal');absent(result,id('Writer'),names.Writer);return relation(result,id('Reader'),'UsedBy','Read',2);
  });
  await ctx.step('delete.top','Delete the outermost caller and verify absence.',()=>deleteOwned(ctx,'Top'));
  await ctx.step('compile.after-delete','Compile after removing the outermost caller.',()=>compile(ctx));
  await ctx.step('references.after-delete','Verify the deleted caller disappears from native cross-references.',async()=>{
    const result=await query(ctx,'Caller');absent(result,id('Top'),names.Top);return {locations:locations(result).length};
  });
  for(const key of ['Caller','Reader','Writer','LocalOnly','DB','Type','Tags'])
    await ctx.step('cleanup.'+key,'Delete only the owned '+key+' fixture and verify absence.',()=>deleteOwned(ctx,key));
  await ctx.step('compile.final','Compile after fixture cleanup.',()=>compile(ctx));
}
function markdown(report) {
  const clean=s=>String(s||'').replace(/[\r\n|]+/g,' ');
  return '# Native V20 cross-reference acceptance\n\nStatus: **'+report.status+'**\n\nProject: '+report.projectPath+'\n\n'+
    '| Scenario | Result | Detail |\n|---|---|---|\n'+report.steps.map(step=>'| '+step.id+' | '+step.status+' | '+clean(step.error||step.description)+' |').join('\n')+
    '\n\nRaw requests, native hierarchy/locations, fixture identities and diagnostics are in report.json. This verifies engineering references on V20, not PLC runtime execution. No save, upload/download, attachment change or automatic write retry. Failed runs retain their recorded fixtures for inspection.\n';
}
async function run(options) {
  assert.ok(!options.resumeReport,'No automatic resume; investigate stopped writes first.');
  const prefix='McpXR_'+randomBytes(6).toString('hex');
  const output=path.resolve(options.output||path.join(__dirname,'../../test-results/native-cross-reference-acceptance',new Date().toISOString().replace(/[:.]/g,'-')+'_'+prefix));
  prepareOutput(output);
  const report={schemaVersion:1,suite:'native-cross-references-v20',status:'running',startedAtUtc:new Date().toISOString(),endpoint:options.endpoint,
    processId:options.processId,projectPath:options.projectPath,prefix,fixture:{},steps:[],calls:[],gaps:[],observedAffectedObjects:[],writesAttempted:false,uncertainWrite:false};
  const persist=()=>writeReport(output,report,markdown);
  const ctx=createContext({...options,onProgress:step=>console.log(step.status.toUpperCase()+': '+step.id)},report,persist);
  try {
    await preflight(ctx,report);assert.equal(report.before.tia.portalVersion,'V20','This acceptance suite targets TIA Portal V20.');await runScenarios(ctx);
    await ctx.step('final-status','Verify the same disposable project remains connected.',async()=>{report.after=await ctx.call('get_status',{processId:ctx.processId});targetStatus(report.after,ctx);});
    report.status='passed';
  } catch(error) {report.status='failed';report.error=error.message;}
  finally {report.finishedAtUtc=new Date().toISOString();persist();}
  return {report,output};
}
module.exports={fixtures,locations,relation,absent,runScenarios,runReferenceScenarios,run,markdown};
