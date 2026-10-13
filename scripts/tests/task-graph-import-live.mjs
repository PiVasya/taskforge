#!/usr/bin/env node
// Opt-in HTTP acceptance against a disposable, empty, hidden root course.
// This exercises real services; it is not a mock and never runs in normal CI.
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import { randomUUID } from 'node:crypto';
import { loadSourceModule } from '../../apps/web/scripts/test-utils/load-source-module.mjs';
const argv=process.argv.slice(2);
const get=name=>argv.includes(name)?argv[argv.indexOf(name)+1]:undefined;
if(argv.includes('--help')) {
  console.log('TASKFORGE_TEST_TOKEN=<editor token> node scripts/tests/task-graph-import-live.mjs --base https://host --course-id <empty hidden ROOT course UUID> --allow-writes [--large 250] [--out /tmp/taskforge-import-live.json]');
  process.exit(0);
}
const base=get('--base'), courseId=get('--course-id'), token=process.env.TASKFORGE_TEST_TOKEN;
if(!base||!courseId||!token||!argv.includes('--allow-writes')) throw Error('Explicit --base, --course-id, --allow-writes and TASKFORGE_TEST_TOKEN are required; no writes performed.');
const url=new URL(base);
if(url.protocol!=='https:'&&!['localhost','127.0.0.1','[::1]'].includes(url.hostname)) throw Error('Non-local origins must use HTTPS.');
if(!/^[a-f\d]{8}-[a-f\d]{4}-[a-f\d]{4}-[a-f\d]{4}-[a-f\d]{12}$/i.test(courseId)) throw Error('Invalid course UUID.');
const large=Number(get('--large')||0); if(![0,250].includes(large)) throw Error('--large accepts 250 only.');
const out=get('--out')||'/tmp/taskforge-import-live.json';
const checks=[];const root=`/api/courses/${courseId}`;
async function request(method,path,body) {
  const response=await fetch(new URL(path,url),{method,headers:{Authorization:`Bearer ${token}`,'Content-Type':'application/json'},body:body===undefined?undefined:JSON.stringify(body),signal:AbortSignal.timeout(120000)});
  const raw=await response.text(); let data; try {data=JSON.parse(raw);}catch {data={code:'NON_JSON_RESPONSE'};}
  return {status:response.status,data};
}
async function ok(method,path,body) {const res=await request(method,path,body);if(res.status<200||res.status>=300)throw Error(`${method} ${path}: HTTP ${res.status} ${res.data?.code||''}`);return res.data;}
async function check(name,fn) {await fn(); checks.push({name,passed:true});console.log(`PASS ${name}`);}
const exported=()=>ok('GET',`${root}/assignments/export-json?includeGuide=false`);
const operationPath=id=>`${root}/assignments/import-json/operations/${id}`;
const json=await loadSourceModule(new URL('../../apps/web/src/features/course-assignments/courseTaskGraphJson.js',import.meta.url));
const model=await loadSourceModule(new URL('../../apps/web/src/features/course-assignments/courseTaskGraphImport.js',import.meta.url));
const mapModel=await loadSourceModule(new URL('../../apps/web/src/features/course-assignments/courseMapModel.js',import.meta.url));
let assignments=[];
async function apply(graph, extra={}) {
  const beforeMap=await ok('GET',`${root}/map`);
  const operationId=randomUUID();
  const options={...json.DEFAULT_JSON_IMPORT_OPTIONS,allowDuplicates:true,allowDeletes:true,allowConnectionDeletes:true,expectedMapVersion:beforeMap.version,...extra};
  const body={operationId,graph,options};
  const receipt=await ok('POST',`${root}/assignments/import-json`,body);
  const repeated=await ok('POST',`${root}/assignments/import-json`,body);
  assert.deepEqual(repeated.assignments.map(x=>x.id),receipt.assignments.map(x=>x.id),'same operation must replay IDs');
  const changed=await request('POST',`${root}/assignments/import-json`,{...body,options:{...options,allowDuplicates:false}});
  assert.equal(changed.status,409);assert.equal(changed.data.code,'IMPORT_OPERATION_PAYLOAD_CHANGED');
  const nodes=beforeMap.document?.nodes||[{id:mapModel.courseNodeId(courseId),entityId:courseId,type:'course',position:{x:0,y:0}}];
  const prepared=model.prepareTaskGraphImport({request:{...receipt,courseId},nodes,edges:beforeMap.document?.edges||[],assignments,courseId});
  assert.equal(prepared.error,undefined,JSON.stringify(prepared));
  const document=mapModel.serializeCourseMap(prepared.nodes,prepared.edges,prepared.viewport||beforeMap.document?.viewport);
  const saved=await ok('PUT',`${operationPath(operationId)}/map`,{expectedVersion:beforeMap.version,document});
  const again=await ok('PUT',`${operationPath(operationId)}/map`,{expectedVersion:beforeMap.version,document});
  assert.equal(again.version,saved.version,'map retry must not advance revision');
  const status=await ok('GET',operationPath(operationId));assert.equal(status.state,'completed');assert.ok(status.audit.mapDiff);
  const actual=await ok('GET',`${root}/map`); assert.equal(actual.version,saved.version);assert.deepEqual(actual.document,saved.document);
  assignments=prepared.assignments;
  return {operationId,receipt,saved,prepared};
}
try {
  const course=await ok('GET',root), initial=await exported();
  assert.ok(!course.parentCourseId,'Use a root course, not a shared parent map.');
  assert.ok(course.isPublic===false||course.isHiddenFromStudents===true,'Use a hidden/private course.');
  assert.equal(initial.tasks?.length||0,0,'Refusing to touch a nonempty course.');
  assert.equal(initial.courses?.length||0,0,'Refusing to touch a course with children.');
  const initialMap=await ok('GET',`${root}/map`);
  assert.ok((initialMap.document?.nodes?.length||0)<=1); assert.equal(initialMap.document?.edges?.length||0,0);
  let mega;
  await check('official mega: one pass, real map receipt, 17 edges, one unplaced draft',async()=>{
    mega=await apply(structuredClone(json.TASK_GRAPH_MEGA_EXAMPLE));
    const result=await exported();assert.equal(result.tasks.length,13);assert.equal(result.courses.length,1);assert.equal(result.connections.length,17);
    assert.equal(mega.prepared.nodes.length,14);assert.equal(mega.prepared.unplacedAssignmentIds.length,1);
  });
  await check('replaying an old completed operation cannot overwrite a later map',async()=>{
    const current=await ok('GET',`${root}/map`);const document=structuredClone(current.document);document.nodes[0].position.x+=17;
    const newer=await ok('PUT',`${root}/map`,{expectedVersion:current.version,document});
    await ok('POST',`${operationPath(mega.operationId)}/resume`);
    const actual=await ok('GET',`${root}/map`);assert.equal(actual.version,newer.version);assert.deepEqual(actual.document,newer.document);
  });
  let branch;
  await check('new branch from populated course keeps previous branches',async()=>{
    const input={schemaVersion:5,format:'taskforge-task-graph',scopes:['content','checks','visibility','connections'],courses:[],
      tasks:[{key:'journal-live-branch',title:'QA journal temporary branch',type:'code-test',language:'python',isVisible:false,
        testCases:[{input:'Привет\r\n  ',expectedOutput:'\u03bb\n'},{input:'',expectedOutput:''}]}],connections:[{from:'$course',to:'journal-live-branch'}]};
    branch=await apply(input,{connectionMode:'merge'}); const after=await exported();assert.equal(after.connections.length,18);
    const task=after.tasks.find(x=>x.id===branch.receipt.assignments[0].id);assert.deepEqual(task.testCases,input.tasks[0].testCases);
  });
  await check('checks-only preserves content and exact test strings',async()=>{
    const id=branch.receipt.assignments[0].id;const before=(await exported()).tasks.find(x=>x.id===id);
    const tests=[{input:'Ж\n ',expectedOutput:'\u4e2d\r\n\t'},{input:'',expectedOutput:'ok  '}];
    await apply({schemaVersion:5,format:'taskforge-task-graph',scopes:['ids','checks'],tasks:[{key:'branch',id,testCases:tests}]});
    const after=(await exported()).tasks.find(x=>x.id===id);assert.equal(after.title,before.title);assert.equal(after.type,before.type);assert.deepEqual(after.testCases,tests);
  });
  await check('invalid reference rejects before any new assignment',async()=>{
    const before=await exported();const invalid={schemaVersion:5,format:'taskforge-task-graph',scopes:['connections'],tasks:[],connections:[{from:'$course',to:'missing'}]};
    const response=await request('POST',`${root}/assignments/import-json`,{operationId:randomUUID(),graph:invalid,options:{}});
    assert.equal(response.status,400);assert.equal((await exported()).tasks.length,before.tasks.length);
  });
  await check('actual deletion removes only the temporary synthetic branch and its edge',async()=>{
    const id=branch.receipt.assignments[0].id;
    await apply({schemaVersion:5,format:'taskforge-task-graph',scopes:['ids'],tasks:[],deleteTasks:[id]});
    const after=await exported();assert.equal(after.tasks.some(x=>x.id===id),false);assert.equal(after.tasks.length,13);assert.equal(after.connections.length,17);
  });
  if(large) await check('250-node application persists all new branches through HTTP re-read',async()=>{
    const tasks=Array.from({length:250},(_,i)=>({key:`journal-load-${i}`,title:`QA load ${i}`,type:'code-test',language:'python',isVisible:false,testCases:[{input:'',expectedOutput:'ok'}]}));
    await apply({schemaVersion:5,format:'taskforge-task-graph',scopes:['content','checks','visibility','connections'],tasks,connections:tasks.map(x=>({from:'$course',to:x.key}))},{connectionMode:'merge'});
    const after=await exported();assert.equal(after.tasks.length,263);assert.equal(after.connections.length,267);
  });
  await fs.writeFile(out,JSON.stringify({courseId,checks,passed:true,largeNodes:large,syntheticDataRetained:true},null,2));
  console.log(`Report: ${out}. Synthetic data remains in the designated QA course.`);
} catch(error) {
  await fs.writeFile(out,JSON.stringify({courseId,checks,passed:false,error:String(error.message).slice(0,1000),syntheticDataMayRemain:true},null,2));
  console.error(`FAIL ${error.message}. Inspect the journal; do not blindly rerun in a nonempty course.`);process.exitCode=1;
}
