#!/usr/bin/env node
// Read-only migration inventory. This is a checklist, NOT a claim of incompatibility.
import fs from 'node:fs';
import path from 'node:path';

export const CHECKS=Object.freeze([
  {id:'advisory_lock',pattern:/pg_(?:try_)?advisory_(?:xact_)?(?:lock|unlock)/i,review:'Check YugabyteDB version and concurrency semantics'},
  {id:'manual_sql',pattern:/NpgsqlCommand|ExecuteSql(?:Raw|Interpolated)|FromSqlRaw|SqlQueryRaw/i,review:'Review provider-specific SQL and transaction retry'},
  {id:'explicit_transaction',pattern:/BeginTransaction(?:Async)?\(|TransactionScope/i,review:'Make complete business transaction replayable/idempotent'},
  {id:'postgres_catalog',pattern:/pg_catalog|pg_locks|pg_stat_|pg_class|information_schema/i,review:'Check PostgreSQL catalog compatibility'},
  {id:'postgres_special',pattern:/\b(?:ctid|xmin|txid_current|pg_notify|pg_listening_channels)\b/i,review:'Check unsupported PostgreSQL-specific semantics'},
  {id:'pgsql_conflict',pattern:/ON CONFLICT|FOR UPDATE|SKIP LOCKED/i,review:'Benchmark and test concurrent workload'},
  {id:'jsonb',pattern:/jsonb/i,review:'Validate JSONB/index and migration DDL'},
]);
export function scanText(rel,content){
 const out=[];
 for(const [index,line] of content.split('\n').entries()){
  for(const x of CHECKS)if(x.pattern.test(line))out.push({path:rel,line:index+1,check:x.id,review:x.review});
 }
 return out;
}
export function scan(root){
 const output=[]; const ignored=new Set(['.git','node_modules','obj','bin','dist','.next']);
 function walk(dir){
  for(const ent of fs.readdirSync(dir,{withFileTypes:true})){
   if(ignored.has(ent.name))continue;
   const full=path.join(dir,ent.name);
   if(ent.isSymbolicLink())continue;
   if(ent.isDirectory()){walk(full);continue}
   if(!ent.isFile()||!(/\.(?:cs|go|sql)$/.test(ent.name)))continue;
   const size=fs.statSync(full).size;
   if(size>2_000_000)continue;
   const rel=path.relative(root,full).split(path.sep).join('/');
   output.push(...scanText(rel,fs.readFileSync(full,'utf8')));
  }
 }
 for(const subdir of ['services','infrastructure'])if(fs.existsSync(path.join(root,subdir)))walk(path.join(root,subdir));
 return output;
}
if(process.argv[1] && path.resolve(process.argv[1])===path.resolve(new URL(import.meta.url).pathname)){
 const root=path.resolve(process.argv[2]||'.');
 if(!fs.existsSync(path.join(root,'services'))) {console.error('Expected a full TaskForge checkout, not the patch archive.');process.exitCode=2}
 else {const findings=scan(root);console.log(JSON.stringify({root,findings,byCheck:Object.fromEntries(CHECKS.map(x=>[x.id,findings.filter(f=>f.check===x.id).length]))},null,2));}
}
