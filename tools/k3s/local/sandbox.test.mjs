import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import {buildLocalManifests,LOCAL_NAMESPACE,LOCAL_IMAGE_PREFIX,LOCAL_IMAGE_TAG} from './generate.mjs';
const here=path.dirname(fileURLToPath(import.meta.url));
const patch=path.resolve(here,'../../../payload/infrastructure/k8s/taskforge-yugabyte');
const installed=path.resolve(here,'../../../infrastructure/k8s/taskforge-yugabyte');
const lab=fs.existsSync(patch)?patch:installed;
const local=path.join(lab,'local');
const read=name=>fs.readFileSync(path.join(local,name),'utf8');

test('dedicated namespace, credentials, images and loopback-only web port',()=>{
 assert.equal(LOCAL_NAMESPACE,'taskforge-k3s-lab');
 assert.equal(LOCAL_IMAGE_PREFIX,'ghcr.io/pivasya/taskforge-k3s-lab');
 assert.equal(LOCAL_IMAGE_TAG,'lab-local-v1');
 assert.match(read('init-secrets.sh'),/taskforge-k3s-lab\/credentials\.env/);
 assert.match(read('init-secrets.sh'),/Database=taskforge_k3s_%s/);
 assert.match(read('init-secrets.sh'),/taskforge-k3s-lab-db\.svc\.cluster\.local/);
 assert.match(read('port-forward.sh'),/--address 127\.0\.0\.1/);
 assert.match(read('port-forward.sh'),/TF_LOCAL_PORT:-18080/);
 assert.doesNotMatch(read('port-forward.sh'),/--address 0\.0\.0\.0/);
});

test('each workload has no cluster-public Service or remote address in its configuration',()=>{
 for(const profile of ['core','all-api']){
  const m=buildLocalManifests(profile);
  const resources=[...m.infra,...m.apps,...m.jobs,...m.web];
  const namespaces=new Set(resources.filter(x=>x.kind==='Namespace').map(x=>x.metadata.name));
  assert.deepEqual([...namespaces],['taskforge-k3s-lab']);
  for(const r of resources){
   if(r.kind==='Namespace')continue;
   assert.equal(r.metadata.namespace,LOCAL_NAMESPACE);
   if(r.kind==='Service')assert.equal(r.spec.type??'ClusterIP','ClusterIP');
  }
  const config=m.infra.find(x=>x.kind==='ConfigMap'&&x.metadata.name==='taskforge-app-config').data;
  for(const [key,val] of Object.entries(config)){
   if(key.startsWith('Services__'))assert.match(val,/^http:\/\/[a-z-]+-api:8080$/);
  }
  assert.equal(config.Jwt__Issuer,'TaskForgeK3sLab');
  assert.equal(config.Cluster__MultiNode,'true');
  assert.equal(config.Cluster__Namespace,LOCAL_NAMESPACE);
  for(const workload of [...m.jobs,...m.apps].filter(x=>x.kind==='Job'||x.kind==='Deployment')){
   const container=workload.spec.template.spec.containers[0];
   const entries=container.env.filter(v=>v.name==='Cluster__Namespace');
   assert.deepEqual(entries,[{name:'Cluster__Namespace',value:LOCAL_NAMESPACE}],workload.metadata.name);
  }
  assert.equal(config.S3__Bucket,'taskforge-k3s-lab-files');
  assert.equal(config.Bootstrap__FirstUserIsAdmin,'false');
 }
});

test('sandbox network policy allows only same namespace, lab YSQL and kube-system DNS',()=>{
 const m=buildLocalManifests('core');
 const p=m.infra.find(x=>x.kind==='NetworkPolicy');
 assert.ok(p);
 assert.deepEqual(p.spec.policyTypes,['Egress']);
 const v=JSON.stringify(p.spec);
 assert.match(v,/taskforge-k3s-lab-db/);
 assert.match(v,/kube-system/);
 assert.match(v,/5433/);
 assert.doesNotMatch(v,/0\.0\.0\.0\/0/);
 assert.doesNotMatch(v,/port":443/);
 assert.doesNotMatch(v,/port":80/);
});

test('every mutating script uses fail-closed local KUBECONFIG guard',()=>{
 for(const name of ['run-local.sh','install-db.sh','create-databases.sh','smoke-write.sh','init-secrets.sh','verify-db.sh','port-forward.sh','build-images.sh']){
  assert.match(read(name),/guard-local-context\.sh/,name);
 }
 const guard=read('guard-local-context.sh');
 assert.match(guard,/KUBECONFIG/);
 assert.match(guard,/127\.0\.0\.1/);
 assert.match(guard,/len\(items\)!=1/);
 assert.match(guard,/k3s-isolated-lab/);
 assert.match(guard,/k3s-isolated-db/);
});

test('YSQL lab database list has no production schema names, RF1 is explicit',()=>{
 const s=fs.readFileSync(path.join(lab,'db/create-k3s-lab-databases.sql'),'utf8');
 assert.match(s,/taskforge_k3s_identity/);
 assert.match(s,/taskforge_k3s_telegram_quiz/);
 assert.doesNotMatch(s,/\('taskforge_identity'\)/);
 assert.match(read('helm-values-single.yaml'),/replicas:\s*\n\s*master: 1\s*\n\s*tserver: 1/);
});
