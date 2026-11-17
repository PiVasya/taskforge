import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import {fileURLToPath} from 'node:url';
import path from 'node:path';
import {CORE_NAMES,OPTIONAL_NAMES,buildLocalManifests,gatewayConfig,writeLocalFiles,LOCAL_IMAGE_PREFIX,LOCAL_IMAGE_TAG} from './generate.mjs';

const root=path.dirname(fileURLToPath(import.meta.url));
const patchLab=path.resolve(root,'../../../payload/infrastructure/k8s/taskforge-yugabyte/local');
const installedLab=path.resolve(root,'../../../infrastructure/k8s/taskforge-yugabyte/local');
const lab=fs.existsSync(patchLab)?patchLab:installedLab;

test('One-node Helm values use RF1 and persistent local-path disks; never pretend RF3',()=>{
 const values=fs.readFileSync(path.join(lab,'helm-values-single.yaml'),'utf8');
 assert.match(values,/replicas:\s*\n\s*master: 1\s*\n\s*tserver: 1/);
 assert.match(values,/enableLoadBalancer: false/);
 assert.match(values,/ephemeral: false/);
 assert.match(values,/storageClass: local-path/);
 assert.doesNotMatch(values,/taskforge.io\/db-node:.*true/);
 const install=fs.readFileSync(path.join(lab,'install-db.sh'),'utf8');
 assert.match(install,/len\(items\)!=1/);
 assert.match(install,/local-development-only/);
});

test('Core deploys six actual DB-owning APIs, front, gateway and the local dependency stores',()=>{
 const m=buildLocalManifests('core');
 assert.equal(CORE_NAMES.length,6);
 assert.equal(OPTIONAL_NAMES.length,6);
 assert.equal(m.jobs.length,6);
 assert.equal(m.apps.filter(x=>x.kind==='Deployment').length,6);
 assert.deepEqual(new Set(m.dbNames),new Set(CORE_NAMES.map(s=>`taskforge_${s.replace('-api','')}`)));
 assert.equal(m.web.filter(x=>x.kind==='Deployment').length,2);
 assert.deepEqual(new Set(m.infra.filter(x=>x.kind==='Deployment').map(x=>x.metadata.name)),new Set(['redis','rabbitmq','minio']));
 assert.equal(m.infra.filter(x=>x.kind==='PersistentVolumeClaim').length,3);
 assert.equal(m.infra.filter(x=>x.kind==='Namespace').length,1);
 assert.equal(m.infra.filter(x=>x.kind==='NetworkPolicy').length,1);
 assert.ok(m.infra.some(x=>x.kind==='ConfigMap'&&x.metadata.name==='taskforge-app-config'));
 for(const obj of m.apps.filter(x=>x.kind==='Deployment')){
  assert.equal(obj.spec.replicas,1);
  assert.equal(obj.spec.template.spec.containers[0].imagePullPolicy,'Never');
 }
 assert.deepEqual(m.web.find(x=>x.metadata.name==='front').spec.template.spec.containers[0].image,`${LOCAL_IMAGE_PREFIX}/front:${LOCAL_IMAGE_TAG}`);
});

test('No stale CT/LLM worker, real Yugabyte mandatory, no external port exposed',()=>{
 for(const profile of ['core','all-api']){
  const m=buildLocalManifests(profile);
  assert.ok(m.jobs.every(x=>x.spec.template.spec.containers[0].env.some(v=>v.name==='Database__RequireYugabyte'&&v.value==='true')));
  const all=[...m.infra,...m.jobs,...m.apps,...m.web];
  assert.ok(!all.some(x=>/(quiz-api|web-ct|front-ct|ai-worker)/.test(x.metadata.name)));
  assert.ok(!all.some(x=>x.kind==='Service'&&['NodePort','LoadBalancer'].includes(x.spec.type)));
  assert.ok(!JSON.stringify(all).includes('Password=real-password'));
 }
});

test('Gateway prioritizes assignment/solution types, routes auth, course, live WS and SPA',()=>{
 const s=gatewayConfig();
 assert.match(s,/resolver __DNS__/);
 assert.doesNotMatch(s,/127\.0\.0\.11/);
 assert.match(s,/tasks-api\.taskforge-k3s-lab\.svc\.cluster\.local/);
 assert.match(s,/solutions-api\.taskforge-k3s-lab\.svc\.cluster\.local/);
 assert.match(s,/identity-api\.taskforge-k3s-lab\.svc\.cluster\.local/);
 assert.match(s,/proxy_set_header Upgrade/);
 assert.ok(s.indexOf('code-assignments')<s.indexOf('location ~* ^/api/(?:courses|groups)'));
 assert.ok(s.indexOf('code-solutions')<s.indexOf('location ~* ^/api/(?:solutions|judge'));
 assert.match(s,/location \/ \{/);
 assert.match(s,/location \^~ \/taskforge-k3s-lab-files\//);
 assert.match(s,/minio\.taskforge-k3s-lab\.svc\.cluster\.local:9000/);
 assert.match(s,/proxy_set_header Host \$http_host/);
});

test('Local generator is deterministic and refuses overwriting existing output',()=>{
 const dir=fs.mkdtempSync(path.join((process.env.TMPDIR||'/tmp'),'tf-k3s-test-'));
 try{
  writeLocalFiles(dir+'/generated','core');
  const a=fs.readFileSync(dir+'/generated/03-apis.json.yaml','utf8');
  assert.throws(()=>writeLocalFiles(dir+'/generated','core'),/Refusing to overwrite/);
  writeLocalFiles(dir+'/again','core');
  const b=fs.readFileSync(dir+'/again/03-apis.json.yaml','utf8');
  assert.equal(a,b);
  assert.throws(()=>buildLocalManifests('invalid'),/Unknown profile/);
 }finally{fs.rmSync(dir,{recursive:true,force:true})}
});

test('gateway ConfigMap mounts as read-only directory, not fragile file subPath',()=>{
 const m=buildLocalManifests('core');
 const gateway=m.web.find(x=>x.kind==='Deployment'&&x.metadata.name==='local-gateway');
 const c=gateway.spec.template.spec.containers[0];
 assert.deepEqual(c.volumeMounts,[{name:'gateway-config',mountPath:'/etc/nginx/taskforge-lab',readOnly:true}]);
 assert.match(c.args.join(' '),/\/etc\/nginx\/taskforge-lab\/site\.conf/);
});

test('K3s runtime scripts use local-only acknowledgement and no app-level multi-write',()=>{
 for(const name of ['install-db.sh','create-databases.sh','init-secrets.sh','run-local.sh']){
  assert.match(fs.readFileSync(path.join(lab,name),'utf8'),/local-development-only/);
 }
 const s=fs.readFileSync(path.join(lab,'init-secrets.sh'),'utf8');
 assert.match(s,/taskforge-db-connections/);
 assert.match(s,/taskforge-app-secrets/);
 assert.match(s,/--from-file=/);
 assert.doesNotMatch(s,/postgres:5432/);
 assert.match(s,/Port=5433/);
});
