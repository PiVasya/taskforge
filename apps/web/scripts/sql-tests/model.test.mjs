import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
const source=await fs.readFile(new URL('../../src/features/sql-task/sqlModel.js',import.meta.url),'utf8');
const m=await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);
const graphSource=await fs.readFile(new URL('../../src/features/course-assignments/courseTaskGraphJson.js',import.meta.url),'utf8');
const graph=await import(`data:text/javascript;base64,${Buffer.from(graphSource).toString('base64')}`);
test('SQL document defaults are separate and immutable between instances',()=>{
 const a=m.freshDataset(),b=m.freshDataset();a.definition.tables.push(m.newTable('items'));assert.equal(b.definition.tables.length,0);
 const s=m.freshSpec();assert.equal(s.mode,'result');assert.equal(s.allowMultipleStatements,false);assert.equal(s.limits.maxRows,1000);
 assert.equal(Object.hasOwn(s,'testsJson'),false);
});

test('database lifecycle authoring defaults and validation stay bounded',()=>{
 const doc=m.freshDataset();
 assert.deepEqual(doc.definition.databases,[]);
 doc.definition.databases=['archive_db','staging_db'];
 assert.deepEqual(m.datasetIssues(doc),[]);
 doc.definition.databases.push('archive_db');
 assert.ok(m.datasetIssues(doc).some(x=>x.includes('Database: archive_db')));
 doc.definition.databases=['tfq_escape'];
 assert.ok(m.datasetIssues(doc).some(x=>x.includes('Database: tfq_escape')));
});
test('rename table preserves exact seed numerics, FK and mappings',()=>{
 const doc=m.freshDataset();doc.definition.tables=[m.newTable('items'),m.newTable('tags')];
 doc.definition.tables[1].foreignKeys=[{name:'fk_tag',columns:['id'],referenceTable:'items',referenceColumns:['id']}];
 doc.seed.items=[{id:'9223372036854775807'}];doc.engineOverrides.mysql={columns:{'items.id':{type:'bigint'}}};
 const next=m.renameTable(doc,0,'products');assert.equal(next.seed.products[0].id,'9223372036854775807');
 assert.equal(next.definition.tables[1].foreignKeys[0].referenceTable,'products');assert.ok(next.engineOverrides.mysql.columns['products.id']);assert.ok(doc.seed.items);
});
test('rename column updates PK, indexes, FK, seed and typed override',()=>{
 const doc=m.freshDataset();doc.definition.tables=[m.newTable('items')];doc.seed.items=[{id:'1'}];
 doc.definition.tables[0].indexes=[{name:'ix_id',columns:['id']}];doc.engineOverrides.sqlite={columns:{'items.id':{type:'integer'}}};
 const next=m.renameColumn(doc,0,0,'item_id');assert.deepEqual(next.definition.tables[0].primaryKey,['item_id']);
 assert.equal(next.seed.items[0].item_id,'1');assert.equal(next.definition.tables[0].indexes[0].columns[0],'item_id');assert.ok(next.engineOverrides.sqlite.columns['items.item_id']);
});
test('save input retains concurrency and stable target order',()=>{
 const spec=m.freshSpec();spec.targets=[{engineProfileId:'a',sort:50},{engineProfileId:'b',sort:10}];
 const input=m.editorInput(spec,'version','stamp');assert.equal(input.concurrencyStamp,'stamp');assert.deepEqual(input.targets.map(x=>x.sort),[0,1]);
});
test('preview snapshot and SQL submission use separate wrapper forms',()=>{
 assert.deepEqual(m.ownSnapshot({sql:{results:[]}}),{results:[]});assert.equal(m.isPending('Preparing'),true);assert.equal(m.isPending('Accepted'),false);
 assert.equal(m.cellText({type:'null',value:null}),'NULL');assert.equal(m.cellText({type:'number',value:'9007199254740993'}),'9007199254740993');
});
test('canonical graph 5 retains legacy versions and shared resource key',()=>{
 assert.equal(graph.TASK_GRAPH_SCHEMA_VERSION,5);assert.equal(graph.TASK_GRAPH_PREVIOUS_SCHEMA_VERSION,4);assert.equal(graph.TASK_GRAPH_LEGACY_SCHEMA_VERSION,3);
 assert.ok(graphSource.includes('datasets'));assert.ok(graphSource.includes('sql-test'));
});

test('engine selection survives unrelated dataset editing and never duplicates a target',()=>{
 const selected=m.toggleEngineTargets([], 'sqlite-profile', true);
 const again=m.toggleEngineTargets(selected, 'sqlite-profile', true);
 assert.equal(again.length,1);
 const doc=m.freshDataset();doc.definition.tables=[m.newTable('products')];
 const changed=m.renameColumn(doc,0,0,'product_id');
 assert.equal(changed.definition.tables[0].columns[0].name,'product_id');
 assert.deepEqual(again,selected);
 assert.deepEqual(m.toggleEngineTargets(again,'sqlite-profile',false),[]);
});

test('dataset catalog refresh is best effort and cannot fail the SQL save workflow', async()=>{
 let applied=null;
 assert.equal(await m.refreshDatasetCatalogBestEffort(async()=>{throw new Error('offline')},v=>{applied=v}),false);
 assert.equal(applied,null);
 assert.equal(await m.refreshDatasetCatalogBestEffort(async()=>[{id:'1'}],v=>{applied=v}),true);
 assert.deepEqual(applied,[{id:'1'}]);
});



test('learner dataset overview is derived from published definition and seed without leaking row values',()=>{
 const definition={databases:['cinema'],tables:[
  {name:'movies',columns:[{name:'id'},{name:'title'},{name:'year'}]},
  {name:'genres',columns:[{name:'id'},{name:'name'}]}
 ]};
 const seed={movies:[{id:'1',title:'Arrival',year:'2016'},{id:'2',title:'Interstellar',year:'2014'}],genres:[{id:'1',name:'Sci-Fi'}]};
 const overview=m.datasetOverview(definition,seed);
 assert.deepEqual(overview.databases,['cinema']);
 assert.equal(overview.totalRows,3);
 assert.deepEqual(overview.tables,[
  {name:'movies',columns:['id','title','year'],rows:2},
  {name:'genres',columns:['id','name'],rows:1}
 ]);
 assert.equal(JSON.stringify(overview).includes('Arrival'),false);
 assert.equal(m.ruCountLabel(1,'таблица','таблицы','таблиц'),'таблица');
 assert.equal(m.ruCountLabel(3,'таблица','таблицы','таблиц'),'таблицы');
 assert.equal(m.ruCountLabel(11,'таблица','таблицы','таблиц'),'таблиц');
 assert.equal(m.ruCountLabel(21,'таблица','таблицы','таблиц'),'таблица');
});

test('publication readiness requires every enabled engine validation receipt',()=>{
 const targets=[{engineProfileId:'sqlite',enabled:true},{engineProfileId:'mysql',enabled:true},{engineProfileId:'pg',enabled:false}];
 assert.equal(m.validationReadyForTargets(targets,[{engineProfileId:'sqlite',datasetStatus:'valid',status:'valid'}]),false);
 assert.equal(m.validationReadyForTargets(targets,[
  {engineProfileId:'sqlite',datasetStatus:'valid',status:'valid'},
  {engineProfileId:'mysql',datasetStatus:'valid',status:'pending'}
 ]),false);
 assert.equal(m.validationReadyForTargets(targets,[
  {engineProfileId:'sqlite',datasetStatus:'valid',status:'valid'},
  {engineProfileId:'mysql',datasetStatus:'valid',status:'valid'}
 ]),true);
});


test('logical SQL engine catalog collapses immutable history without rewriting selected drafts',()=>{
 const profiles=[
  {id:'sqlite-new',engine:'sqlite',displayName:'SQLite 3',fingerprint:'new'},
  {id:'sqlite-old',engine:'sqlite',displayName:'SQLite 3',fingerprint:'old'},
  {id:'mysql-new',engine:'mysql',displayName:'MySQL 8',fingerprint:'mysql'}
 ];
 const selected=[{engineProfileId:'sqlite-old',enabled:true,sort:0,starterSqlOverride:'select 1'}];
 const rows=m.logicalEngineProfiles(profiles,selected,new Set(['new','old','mysql']));
 assert.equal(rows.length,2);
 const sqlite=rows.find(x=>x.engine==='sqlite');
 assert.equal(sqlite.activeProfile.id,'sqlite-old');
 assert.equal(sqlite.preferredProfile.id,'sqlite-new');
 assert.equal(sqlite.online,true);
 assert.equal(sqlite.canUpgrade,false);
});

test('offline historical target can be explicitly moved to newest online profile preserving overrides',()=>{
 const profiles=[
  {id:'pg-new',engine:'postgresql',displayName:'PostgreSQL 16',fingerprint:'new'},
  {id:'pg-old',engine:'postgresql',displayName:'PostgreSQL 16',fingerprint:'old'}
 ];
 const targets=[{engineProfileId:'pg-old',enabled:true,sort:4,referenceSqlOverride:'select 42'}];
 const [row]=m.logicalEngineProfiles(profiles,targets,new Set(['new']));
 assert.equal(row.canUpgrade,true);
 const changed=m.replaceEngineTargetProfile(targets,'pg-old','pg-new');
 assert.equal(changed[0].engineProfileId,'pg-new');
 assert.equal(changed[0].referenceSqlOverride,'select 42');
 assert.equal(changed[0].sort,4);
});

test('SQL publication errors are presented as compact preparation state without server text leakage',()=>{
 const error={response:{status:409,data:{code:'SQL_NOT_PUBLISHED',message:'Every enabled engine must pass validation before publication or execution.'}}};
 const view=m.sqlErrorPresentation(error,error.response.data.message);
 assert.equal(view.code,'SQL_NOT_PUBLISHED');
 assert.equal(view.title,'SQL-\u0437\u0430\u0434\u0430\u043d\u0438\u0435 \u0432\u0440\u0435\u043c\u0435\u043d\u043d\u043e \u043d\u0435\u0434\u043e\u0441\u0442\u0443\u043f\u043d\u043e');
 assert.equal(view.detail.includes('Every enabled engine'),false);
});

test('SQL runtime failures are presented as service unavailability',()=>{
 const error={response:{status:503,data:{code:'SQL_SERVICE_UNAVAILABLE',message:'upstream failed'}}};
 const view=m.sqlErrorPresentation(error,error.response.data.message);
 assert.equal(view.code,'SQL_SERVICE_UNAVAILABLE');
 assert.equal(view.title,'SQL \u043d\u0435\u0434\u043e\u0441\u0442\u0443\u043f\u0435\u043d');
 assert.equal(view.detail.includes('upstream failed'),false);
});

test('unknown SQL errors keep a safe generic title and supplied user-facing detail',()=>{
 const error={response:{status:400,data:{code:'SQL_DOCUMENT_INVALID'}}};
 const view=m.sqlErrorPresentation(error,'\u041d\u0435\u0432\u0435\u0440\u043d\u044b\u0435 \u0434\u0430\u043d\u043d\u044b\u0435');
 assert.equal(view.title,'\u041d\u0435 \u0443\u0434\u0430\u043b\u043e\u0441\u044c \u043e\u0442\u043a\u0440\u044b\u0442\u044c SQL-\u0437\u0430\u0434\u0430\u043d\u0438\u0435');
 assert.equal(view.detail,'\u041d\u0435\u0432\u0435\u0440\u043d\u044b\u0435 \u0434\u0430\u043d\u043d\u044b\u0435');
});

test('SQL editor polling observes automatic first publication and new concurrency stamp',()=>{
 const current={draftVersionId:'draft-1',publishedVersionId:null,concurrencyStamp:'old',validation:[1],profiles:[1],local:'keep'};
 const latest={draftVersionId:'draft-1',publishedVersionId:'draft-1',concurrencyStamp:'new',validation:[2],profiles:[2]};
 const merged=m.mergeSqlEditorPoll(current,latest);
 assert.equal(merged.publishedVersionId,'draft-1');
 assert.equal(merged.concurrencyStamp,'new');
 assert.deepEqual(merged.validation,[2]);
 assert.equal(merged.local,'keep');
 assert.equal(m.mergeSqlEditorPoll(current,{...latest,draftVersionId:'draft-2'}),current);
});


test('learner SQL action dock exposes check only and never renders a separate run action', async()=>{
 const solveSource=await fs.readFile(new URL('../../src/features/sql-task/SqlTaskSolve.jsx',import.meta.url),'utf8');
 assert.ok(solveSource.includes('primaryLabel="Проверить"'));
 assert.ok(solveSource.includes("onPrimary={() => void submit('check')}"));
 assert.equal(solveSource.includes("key: 'sql-run'"),false);
 assert.equal(solveSource.includes("label: 'Запустить'"),false);
});
