#!/usr/bin/env node
import fs from 'node:fs';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import {render,toYaml,DATABASE_SERVICES,BACKGROUND_DB_CONSUMERS,EXTRA_DATABASES} from './manifest.mjs';

function parse(argv){
 const opts={namespace:'taskforge',replicas:2,release:'',imageTag:'',imagePrefix:'',imagePullSecret:'ghcr-pull',outDir:''};
 const fields={'--namespace':'namespace','--replicas':'replicas','--release':'release','--image-tag':'imageTag','--image-prefix':'imagePrefix','--image-pull-secret':'imagePullSecret','--out-dir':'outDir'};
 for(let i=0;i<argv.length;i+=2){
   if(!(argv[i] in fields)||!argv[i+1])throw new Error(`Invalid argument: ${argv[i]||'(none)'}`);
   const key=fields[argv[i]];
   if(opts[key]&&!['namespace','replicas','imagePullSecret'].includes(key))throw new Error(`Duplicate argument: ${argv[i]}`);
   opts[key]=argv[i+1];
 }
 opts.replicas=Number(opts.replicas);
 if(!opts.outDir)throw new Error('Require --out-dir');
 return opts;
}
function main(){
 const opts=parse(process.argv.slice(2));
 const {jobs,apps}=render(opts);
 const out=path.resolve(opts.outDir);
 if(fs.existsSync(out)&&fs.readdirSync(out).length)throw new Error(`Output directory is not empty: ${out}`);
 fs.mkdirSync(out,{recursive:true});
 fs.writeFileSync(path.join(out,'01-db-migrations.yaml'),toYaml(jobs));
 fs.writeFileSync(path.join(out,'02-api-deployments.yaml'),toYaml(apps));
 fs.writeFileSync(path.join(out,'database-ownership.json'),JSON.stringify({services:DATABASE_SERVICES,backgroundConsumers:BACKGROUND_DB_CONSUMERS,extraDatabases:EXTRA_DATABASES},null,2)+'\n');
 console.log(`Rendered ${jobs.length} migration Jobs + ${apps.length} Kubernetes workload/service objects into ${out}`);
 console.log('STOP: apply Jobs, wait for all completed, then deploy APIs. No secrets are generated.');
}
if(process.argv[1] && path.resolve(process.argv[1])===fileURLToPath(import.meta.url)){
 try{main()}catch(e){console.error(`ERROR: ${e.message}`);process.exitCode=1}
}
