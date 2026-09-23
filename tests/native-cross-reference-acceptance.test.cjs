'use strict';
const {test}=require('node:test');const assert=require('node:assert/strict');
const {fixtures,locations,relation,absent}=require('./native-acceptance/cross-reference-scenarios.cjs');
const response=(rows)=>({complete:true,errors:[],sources:[{objectId:'db',name:'DB',children:[{name:'Data.Flag',objectId:null,children:[],references:rows}],references:[]}]});
const ref=(id,access,where,type='UsedBy')=>({objectId:id,name:id,locations:[{referenceType:type,access,referenceLocation:where,referencedAsObjectId:null}]});
test('fixture graph has isolated global/local symbol names, user constant, UDT member access and two call levels',()=>{
 const {names,specs}=fixtures('McpXR_0123456789ab');assert.equal(specs.length,7);assert.equal(new Set(specs.map(s=>s.name)).size,7);
 const by=key=>specs.find(s=>s.key===key);
 assert.ok(by('LocalOnly').content.includes('#"'+names.Signal+'"'));
 assert.ok(by('Reader').content.includes('"'+names.Limit+'"'));
 assert.equal(by('Reader').content.split('"'+names.Signal+'"').length-1,2);
 assert.ok(by('Caller').content.includes('"'+names.Reader+'"('));assert.ok(by('Top').content.includes('"'+names.Caller+'"();'));
 assert.ok(by('DB').content.includes('Data : "'+names.Type+'"'));
 assert.ok(by('Writer').content.includes('"'+names.Signal+'"'));assert.ok(!by('Writer').updated.includes(names.Signal));
 assert.ok(by('Writer').updated.includes('Data.Count'));
 assert.throws(()=>fixtures('unowned"name'),/match/);
});
test('reference assertions retain child ancestry, null member IDs and multiple distinct native locations',()=>{
 const result=response([ref('reader','Read','@Reader line1'),ref('reader','Read','@Reader line2')]);
 assert.equal(locations(result)[0].ancestry[0].objectId,'db');assert.equal(locations(result)[0].source.objectId,null);
 const evidence=relation(result,'reader','UsedBy','Read',2);assert.equal(evidence.length,2);assert.equal(evidence[0].sourceId,null);
});
test('references require matching native target, direction and access, not matching text alone',()=>{
 const result=response([ref('other','Write','@reader UsedBy Read'),ref('reader','Read','line','Uses')]);
 assert.throws(()=>relation(result,'reader','UsedBy','Read'),/Missing/);
 assert.throws(()=>relation(result,'other','UsedBy','Read'),/Missing/);
});
test('repeated native SCL location labels retain occurrences while missing or blank locations fail',()=>{
 assert.equal(relation(response([ref('reader','Read','@Reader ▶ Program code'),ref('reader','Read','@Reader ▶ Program code')]),'reader','UsedBy','Read',2).length,2);
 assert.throws(()=>relation(response([ref('reader','Read','same')]),'reader','UsedBy','Read',2),/Missing/);
 assert.throws(()=>relation(response([ref('reader','Read','')]),'reader','UsedBy','Read'),/location/);
});
test('freshness and shadowing assertions reject surviving IDs and same-name references',()=>{
 const result=response([ref('replacement','Read','line')]);assert.throws(()=>absent(result,'old','replacement'),/still appears/);
 assert.throws(()=>absent(result,'replacement','other'),/still appears/);absent(result,'gone','gone');
});
test('partial or unreadable native reference trees cannot become empty or passing relationships',()=>{
 assert.throws(()=>locations({complete:false,errors:[],sources:[]}),/equal/);
 assert.throws(()=>locations({complete:true,errors:[],sources:null}));
 const result=response([]);result.sources[0].children[0].references=null;assert.throws(()=>locations(result));
 assert.deepEqual(locations({complete:true,errors:[],sources:[]}),[]);
});
