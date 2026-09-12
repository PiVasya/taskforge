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
