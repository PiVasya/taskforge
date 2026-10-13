import test from 'node:test';
import assert from 'node:assert/strict';
import { loadSourceModule } from '../test-utils/load-source-module.mjs';
const { createTaskGraphJournalClient: create } = await loadSourceModule(new URL('../../src/features/course-assignments/taskGraphJournalClient.js', import.meta.url));
const helpers = await loadSourceModule(new URL('../../src/features/course-assignments/courseTaskGraphOperation.js', import.meta.url));
const { captureTaskGraphPreviewSnapshot: snapshot } = await loadSourceModule(new URL('../../src/features/course-assignments/taskGraphPreviewSnapshot.js', import.meta.url));
const uid = n => `00000000-0000-4000-8000-${String(n).padStart(12, '0')}`;
const deferred = () => { let resolve, reject; const promise = new Promise((a,b) => { resolve=a; reject=b; }); return { promise, resolve, reject }; };
const missing = () => Object.assign(new Error('Not found'), { response: { status: 404 } });
const graph = { schemaVersion: 5, format: 'taskforge-task-graph', tasks: [{key:'a'}] };
function fixture(extra={}) {
  const memory = new Map(); let next = 1; const rows = new Map(); const calls = { begin:0, resume:0, stop:0 };
  const storage = { getItem:k=>memory.get(k), setItem:(k,v)=>memory.set(k,v) };
  const api = {
    list: async()=>[...rows.values()],
    get: async (course,id)=> { if(!rows.has(id)) throw missing(); return structuredClone(rows.get(id)); },
    begin: async (course,data,options,id)=> { calls.begin++; const response={createdCount:1,taskGraph:{tasks:[]},operationState:'data-committed'}; rows.set(id,{operationId:id,courseId:course,state:'data-committed',response}); return response; },
    resume: async(course,id)=>{ calls.resume++; rows.get(id).state='completed'; },
    stop: async(course,id)=>{ calls.stop++; rows.get(id).state='stopped'; },
  };
  const args={courseId:uid(999),userId:uid(998),storage,cryptoProvider:{randomUUID:()=>uid(next++)},api,...extra};
  return {client:create(args), args, api, rows, calls, memory};
}

test('initial journal lookup is read-only and must finish before a mutation', async()=>{
  const f=fixture(); await assert.rejects(f.client.begin(graph,{})); assert.equal(f.calls.begin,0);
  await f.client.discover(); assert.equal(f.client.getSnapshot().ready,true); assert.equal(f.calls.begin,0);
});
test('disabled or anonymous controller cannot begin',async()=>{
  for(const values of [{enabled:false},{userId:null}]) { const f=fixture(values); await f.client.discover(); await assert.rejects(f.client.begin(graph,{})); assert.equal(f.calls.begin,0); }
});
test('lost POST acknowledgement reconciles committed data without a second POST',async()=>{
  const f=fixture(); const save=f.api.begin; f.api.begin=async(...args)=>{await save(...args);throw new Error('network timeout');};
  await f.client.discover(); await assert.rejects(f.client.begin(graph,{}));
  assert.equal(f.calls.begin,1); assert.equal(f.client.getSnapshot().operation.state,'data-committed');
  await assert.rejects(f.client.begin(graph,{})); assert.equal(f.calls.begin,1);
  await f.client.resume(); assert.equal(f.calls.begin,1); assert.equal(f.calls.resume,1); assert.equal(f.client.getSnapshot().operation.state,'completed');
});
test('double click sends exactly one original request',async()=>{
  const f=fixture(), wait=deferred(), original=f.api.begin; f.api.begin=async(...args)=>{await wait.promise;return original(...args);};
  await f.client.discover(); const first=f.client.begin(graph,{});
  await assert.rejects(f.client.begin(graph,{})); wait.resolve(); await first; assert.equal(f.calls.begin,1);
});
test('reload restores the same server operation and stores no payload in browser storage',async()=>{
  const f=fixture(); await f.client.discover(); const op=await f.client.begin(graph,{});
  const reloaded=create(f.args); await reloaded.discover(); assert.equal(reloaded.getSnapshot().operation.operationId,op.operationId);
  assert.deepEqual([...f.memory.values()],[op.operationId]); assert.equal(f.calls.begin,1);
});
test('server history restores an import when localStorage is unavailable',async()=>{
  const f=fixture({storage:{getItem(){throw Error('denied');},setItem(){throw Error('denied');}}});
  await f.client.discover(); const op=await f.client.begin(graph,{});
  const reloaded=create(f.args); await reloaded.discover(); assert.equal(reloaded.getSnapshot().operation.operationId,op.operationId);
});
test('404 after an uncertain send reuses the old operationId on explicit resubmission',async()=>{
  const f=fixture(); f.memory.set(helpers.importPointerKey(f.args.userId,f.args.courseId),uid(42));
  await f.client.discover(); assert.equal(f.client.getSnapshot().operation.state,'missing');
  const op=await f.client.begin(graph,{}); assert.equal(op.operationId,uid(42)); assert.equal(f.calls.begin,1);
});
test('server pending operation takes priority over an orphan browser pointer',async()=>{
  const f=fixture(); f.memory.set(helpers.importPointerKey(f.args.userId,f.args.courseId),uid(42));
  f.rows.set(uid(3),{operationId:uid(3),courseId:f.args.courseId,state:'prepared'});
  await f.client.discover(); assert.equal(f.client.getSnapshot().operation.operationId,uid(3));
});
test('another-tab pending conflict selects the operation identified by the server',async()=>{
  const f=fixture(); await f.client.discover();
  f.rows.set(uid(70),{operationId:uid(70),courseId:f.args.courseId,state:'prepared'});
  f.api.begin=async()=>{throw Object.assign(new Error('conflict'),{response:{status:409,data:{code:'IMPORT_PREVIOUS_PENDING',operationId:uid(70)}}});};
  await assert.rejects(f.client.begin(graph,{})); assert.equal(f.client.getSnapshot().operation.operationId,uid(70));
});
test('unavailable initial journal fails closed and can be retried',async()=>{
  const f=fixture(), list=f.api.list; f.api.list=async()=>{throw Error('offline');};
  await f.client.discover(); assert.equal(f.client.getSnapshot().ready,false); await assert.rejects(f.client.begin(graph,{}));
  f.api.list=list; await f.client.discover(); assert.equal(f.client.getSnapshot().ready,true);
});
test('reload with committed map performs no automatic writes',async()=>{
  const f=fixture(); f.rows.set(uid(10),{operationId:uid(10),courseId:f.args.courseId,state:'completed'});
  await f.client.discover(); assert.equal(f.client.getSnapshot().operation.state,'completed'); assert.deepEqual(f.calls,{begin:0,resume:0,stop:0});
});
test('new intentional import after completion gets a fresh operationId',async()=>{
  const f=fixture(); await f.client.discover(); const first=await f.client.begin(graph,{}); await f.client.resume(); const second=await f.client.begin(graph,{});
  assert.notEqual(first.operationId,second.operationId);
});
test('explicit stop permits a new import without replaying original mutations',async()=>{
  const f=fixture(); await f.client.discover(); const first=await f.client.begin(graph,{}); await f.client.stop();
  assert.equal(f.client.getSnapshot().operation.state,'stopped'); const second=await f.client.begin(graph,{});
  assert.notEqual(second.operationId,first.operationId); assert.equal(f.calls.stop,1);
});
test('late status read cannot replace a newer operation',async()=>{
  const f=fixture(); await f.client.discover(); const first=await f.client.begin(graph,{}); await f.client.resume();
  const wait=deferred(),get=f.api.get; f.api.get=async(course,id)=>id===first.operationId?wait.promise:get(course,id);
  const oldRead=f.client.refresh(first.operationId); const second=await f.client.begin(graph,{});
  wait.resolve({operationId:first.operationId,courseId:f.args.courseId,state:'completed'}); await oldRead;
  assert.equal(f.client.getSnapshot().operation.operationId,second.operationId);
});
test('different user/course instances have isolated browser pointers',async()=>{
  const f=fixture(); await f.client.discover(); await f.client.begin(graph,{});
  const second=fixture({courseId:uid(40),userId:uid(41),storage:f.args.storage}); await second.client.discover();
  assert.equal(second.client.getSnapshot().operation,null);
  assert.notEqual(helpers.importPointerKey(uid(1),uid(2)),helpers.importPointerKey(uid(2),uid(1)));
});
test('unknown outcome and map-pending are not completed; stopped is not pending',()=>{
  for(const state of ['prepared','sending','unknown','missing','data-failed','data-committed','map-pending','map-conflict','map-rejected']) assert.equal(helpers.isImportPending({state,operationId:uid(1)}),true,state);
  for(const state of ['completed','stopped']) assert.equal(helpers.isImportPending({state,operationId:uid(1)}),false,state);
});
test('same saved operation resumes only by ID; original graph/options are not resent',async()=>{
  const f=fixture(); await f.client.discover(); const op=await f.client.begin(graph,{}); let args;
  f.api.resume=async(...values)=>{args=values;}; await f.client.resume();
  assert.deepEqual(args,[f.args.courseId,op.operationId]); assert.equal(f.calls.begin,1);
});
test('map recovery has real IDs and excludes already committed deletions/content/checks',()=>{
  const op={operationId:uid(3),response:{taskGraph:{courses:[{key:'sub',id:uid(4),title:'Sub'}],tasks:[{key:'a',assignmentId:uid(1),course:'sub'}],
    deleteTasks:[uid(2)],connections:[{from:'$course',to:'a'}],layout:{positions:{a:{x:10,y:20}}},apply:{connections:true,connectionAccess:true,layout:true}}}};
  const payload=helpers.mapRecoveryPayload(op);
  assert.deepEqual(payload.scopes,['ids','connections','connectionAccess','layout']); assert.equal(payload.deleteTasks,undefined);
  assert.deepEqual(payload.tasks,[{key:'a',id:uid(1)}]); assert.equal(payload.tasks[0].course,undefined);
  assert.equal(payload.tasks[0].title,undefined); assert.equal(payload.tasks[0].testCases,undefined);
});
test('disabled map sections remain disabled during recovery',()=>{
  const payload=helpers.mapRecoveryPayload({response:{taskGraph:{tasks:[],apply:{connections:false,connectionAccess:true,layout:false}}}});
  assert.deepEqual(payload.scopes,['ids','connectionAccess']);
});
test('operation request keeps same operation ID and allows only explicit attempt metadata',()=>{
  const op={operationId:uid(1),courseId:uid(2),response:{taskGraph:{tasks:[]},assignments:[]}};
  const request=helpers.mapRequestFromOperation(op,{attempt:2,expectedMapVersion:7});
  assert.equal(request.key,uid(1)); assert.equal(request.operationId,uid(1)); assert.equal(request.expectedMapVersion,7);
});
test('audit excludes secrets in raw content, unknown options, SQL and HTTP diagnostics',()=>{
  const secret='DO_NOT_EXPORT_SECRET';
  const op={operationId:uid(1),requestHash:'a'.repeat(64),audit:{scopes:['ids','password'],options:{updateChecks:false,password:secret}},
    error:{code:'IMPORT_UNCONFIRMED',message:secret,headers:{Authorization:secret}},
    response:{assignments:[{title:secret,testCases:[{expectedOutput:secret}],sql:{referenceSql:secret}}],createdCount:1,
      entityChanges:[{assignmentId:uid(2),action:'created',afterSha256:'b'.repeat(64),changedFields:['testCases'],raw:secret}]},
    mapResponse:{version:1,document:{nodes:[{id:'x',entityId:uid(2),type:'assignment',position:{x:0,y:0,password:secret}}],edges:[{source:'x',target:'y',settings:{token:secret}}]}}};
  const report=helpers.buildTaskGraphAudit(op); const text=JSON.stringify(report);
  assert.ok(!text.includes(secret)); assert.equal(report.rawPayloadIncluded,false); assert.deepEqual(report.scopes,['ids']);
  assert.deepEqual(report.options,{updateChecks:false}); assert.equal(report.applied.entityChanges[0].afterSha256,'b'.repeat(64));
});
test('audit captures server-assigned IDs and hashes without requiring a surviving preview',()=>{
  const report=helpers.buildTaskGraphAudit({operationId:uid(1),state:'completed',requestHash:'c'.repeat(64),
    response:{createdCount:1,taskGraph:{tasks:[{key:'x',assignmentId:uid(2),action:'created'}]}}});
  assert.equal(report.preview,null); assert.equal(report.applied.tasks[0].assignmentId,uid(2)); assert.equal(report.requestSha256,'c'.repeat(64));
});
test('preview snapshot rejects a map revision change during export',async()=>{
  let reads=0; await assert.rejects(snapshot({readMap:async()=>({version:++reads}),readExport:async()=>({})}),e=>e.code==='IMPORT_PREVIEW_CHANGED');
});
test('preview snapshot uses the actual stable server version',async()=>{
  const currentExport={tasks:[]}; const result=await snapshot({readMap:async()=>({version:17}),readExport:async()=>currentExport});
  assert.equal(result.mapVersion,17); assert.equal(result.currentExport,currentExport);
});
test('preview cannot silently use version zero when server lookup fails',async()=>{
  await assert.rejects(snapshot({readMap:async()=>{throw Error('unavailable');},readExport:async()=>({})}));
});

test('saved graph diff remains available without a browser preview',()=>{
  const diff={previousVersion:7,savedVersion:8,nodes:{added:['n1'],removed:['n2'],changed:['n3']},edges:{added:['e1'],removed:['e2'],changed:[]}};
  assert.deepEqual(helpers.buildTaskGraphAudit({audit:{mapDiff:diff}}).mapDiff,diff);
});

test('documentation and application explain identical operation semantics',async()=>{
  const fs=await import('node:fs/promises');
  const guide=JSON.parse(await fs.readFile(new URL('../../../../docs/import-export/expected-behavior.json',import.meta.url),'utf8'));
  const model=await loadSourceModule(new URL('../../src/features/course-assignments/courseTaskGraphJson.js',import.meta.url));
  assert.deepEqual(model.TASK_GRAPH_BEHAVIOR_GUIDE,guide.rules);
  for(const key of ['operationIdentity','resume','mapConflict','audit','stop']) assert.ok(guide.rules[key]);
});
