#!/usr/bin/env node
// Real single-node K3s starter. Local-only defaults. This is NOT the RF3 profile.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  DATABASE_SERVICES,
  render,
  toYaml,
} from '../manifest.mjs';

export const CORE_NAMES = Object.freeze([
  'identity-api','education-api','content-api','tasks-api','solutions-api','execution-api'
]);
export const OPTIONAL_NAMES = Object.freeze(DATABASE_SERVICES.map(x=>x.name).filter(x=>!CORE_NAMES.includes(x)));
export const LOCAL_IMAGE_PREFIX='ghcr.io/pivasya/taskforge-k3s-lab';
export const LOCAL_IMAGE_TAG='lab-local-v1';
export const LOCAL_RELEASE='lab1';
export const LOCAL_NAMESPACE='taskforge-k3s-lab';
const resource=(kind,name,spec,extra={})=>({apiVersion:kind==='Namespace'?'v1':kind==='Deployment'?'apps/v1':'v1',kind,metadata:{name,...(kind==='Namespace'?{}:{namespace:LOCAL_NAMESPACE})},...extra,...(spec?{spec}:{})});
const envSecret=(name,key)=>({name,valueFrom:{secretKeyRef:{name:'taskforge-app-secrets',key}}});
const dep=(name,image,port,opts={})=>{
 const c={name,image,imagePullPolicy:opts.local?'Never':'IfNotPresent',ports:[{containerPort:port,name:'http'}],resources:{requests:{cpu:'50m',memory:'96Mi'},limits:{memory:opts.memory||'512Mi'}}};
 if(opts.command)c.command=opts.command;
 if(opts.args)c.args=opts.args;
 if(opts.env)c.env=opts.env;
 if(opts.mount){c.volumeMounts=[{name:'data',mountPath:opts.mount}];}
 if(opts.config){c.volumeMounts=[{name:'gateway-config',mountPath:'/etc/nginx/taskforge-lab',readOnly:true}];}
 if(opts.ready)c.readinessProbe={httpGet:{path:opts.ready,port:'http'},periodSeconds:5,initialDelaySeconds:3};
 const pod={containers:[c],...(opts.mount?{volumes:[{name:'data',persistentVolumeClaim:{claimName:`${name}-data`}}]}:{}),...(opts.config?{volumes:[{name:'gateway-config',configMap:{name:'local-gateway-config'}}]}:{})};
 return {apiVersion:'apps/v1',kind:'Deployment',metadata:{namespace:LOCAL_NAMESPACE,name},spec:{replicas:1,selector:{matchLabels:{app:name}},strategy:{type:'Recreate'},template:{metadata:{labels:{app:name}},spec:pod}}};
};
const svc=(name,port,target=port,more=[])=>({apiVersion:'v1',kind:'Service',metadata:{namespace:LOCAL_NAMESPACE,name},spec:{selector:{app:name},ports:[{name:'main',port,targetPort:target},...more]}});
const pvc=(name,size)=>({apiVersion:'v1',kind:'PersistentVolumeClaim',metadata:{namespace:LOCAL_NAMESPACE,name:`${name}-data`},spec:{accessModes:['ReadWriteOnce'],storageClassName:'local-path',resources:{requests:{storage:size}}}});
const configMap=(name,data)=>({apiVersion:'v1',kind:'ConfigMap',metadata:{namespace:LOCAL_NAMESPACE,name},data});
const configEnv={
  ASPNETCORE_ENVIRONMENT:'Development',
  ASPNETCORE_URLS:'http://+:8080',
  TASKFORGE_DEBUG_LOGS:'0',
  Cluster__MultiNode:'true',
  Cluster__Namespace:LOCAL_NAMESPACE,
  Database__RequireYugabyte:'true',
  Database__MigrateOnStartup:'false',
  Database__EnsureCreated:'false',
  Database__MigrationJobOnly:'false',
  Cache__Enabled:'true',
  Redis__Host:'redis',
  RabbitMq__Host:'rabbitmq',
  RabbitMq__UserName:'taskforge',
  S3__Endpoint:'http://minio:9000',
  S3__PublicEndpoint:'http://127.0.0.1:18080',
  S3__Bucket:'taskforge-k3s-lab-files',
  S3__UsePathStyle:'true',
  S3__Region:'us-east-1',
  CodeAnalyzer__Enabled:'false',
  Browser__Enabled:'false',
  AiRemoteBrowser__Enabled:'false',
  Seed__InitialCatalog:'false',
  Jwt__Issuer:'TaskForgeK3sLab',
  Jwt__Audience:'TaskForgeK3sLab',
  Bootstrap__FirstUserIsAdmin:'false',
};
for(const s of DATABASE_SERVICES){const key=s.name.replaceAll('-api','');const proper=key==='ai'?'Ai':key[0].toUpperCase()+key.slice(1);configEnv[`Services__${proper}Api`]=`http://${s.name}:8080`;}
// Also the service aliases used by legacy cross-service HTTP calls.
Object.assign(configEnv,{Services__TasksApi:'http://tasks-api:8080',Services__SolutionsApi:'http://solutions-api:8080',Services__IdentityApi:'http://identity-api:8080',Services__EducationApi:'http://education-api:8080',Services__ExecutionApi:'http://execution-api:8080',Services__FilesApi:'http://files-api:8080'});

const routes=[
 ['^/api/sql(?:/|$)','tasks-api'],
 ['^/api/courses/[^/]+/(?:code-assignments|image-assignments|test-assignments|math-assignments|sql-assignments)(?:/|$)','tasks-api'],
 ['^/api/(?:code-assignments|image-assignments|test-assignments|math-assignments|sql-assignments)(?:/|$)','tasks-api'],
 ['^/api/(?:code-solutions|image-solutions|sql-solutions)(?:/|$)','solutions-api'],
 ['^/api/courses/[^/]+/(?:learning-map|assignments)(?:/|$)','tasks-api'],
 ['^/api/admin/users/[^/]+/groups(?:/|$)','education-api'],
 ['^/api/admin/users/[^/]+/(?:test-attempts|math-attempts)(?:/|$)','tasks-api'],
 ['^/api/admin/users/[^/]+/(?:solutions|image-solutions)(?:/|$)','solutions-api'],
 ['^/api/me/ui-settings(?:/|$)','identity-api'],
 ['^/api/admin/solution-events$','solutions-api'],
 ['^/api/admin/task-solution-events$','tasks-api'],
 ['^/api/admin/solution-users(?:/|$)','solutions-api'],
 ['^/api/(?:auth|users|profile)(?:/|$)','identity-api'],
 ['^/api/admin/(?:users|feature-roles)(?:/|$)','identity-api'],
 ['^/api/integrations/(?:telegram|discord|messengers)(?:/|$)','identity-api'],
 ['^/api/(?:courses|groups)(?:/|$)','education-api'],
 ['^/api/admin/groups(?:/|$)','education-api'],
 ['^/api/learning(?:/|$)','content-api'],
 ['^/api/admin/learning(?:/|$)','content-api'],
 ['^/api/(?:assignments|tests)(?:/|$)','tasks-api'],
 ['^/api/admin/(?:assignments|test-attempts|math-attempts)(?:/|$)','tasks-api'],
 ['^/api/me/(?:test-attempts|math-attempts)(?:/|$)','tasks-api'],
 ['^/api/(?:solutions|judge|judge2|leaderboard|badges|quotas)(?:/|$)','solutions-api'],
 ['^/api/me/(?:solutions|image-solutions|quotas)(?:/|$)','solutions-api'],
 ['^/api/admin/(?:solutions|image-solutions|leaderboard|rating)(?:/|$)','solutions-api'],
 ['^/api/(?:compiler|image-runners|execution)(?:/|$)','execution-api'],
 ['^/api/admin/ai/(?:account-manager|investigation)(?:/|$)','ai-api'],
 ['^/api/support(?:/|$)','support-api'],
 ['^/api/telegram(?:/|$)','support-api'],
 ['^/api/minecraft(?:/|$)','minecraft-api'],
 ['^/api/integrations/minecraft(?:/|$)','minecraft-api'],
 ['^/api/admin/minecraft-links(?:/|$)','minecraft-api'],
 ['^/api/(?:files|private-files)(?:/|$)','files-api'],
 ['^/api/notifications(?:/|$)','notifications-api'],
 ['^/api/(?:activity|system-status)(?:/|$)','observability-api'],
 ['^/api/admin/(?:activity|system-status|cluster|analytics)(?:/|$)','observability-api'],
];

export function gatewayConfig(){
 const base=`# One-node K3s proxy. DNS uses the pod resolver, not the hardcoded Docker resolver.\n`+
 `server {\n  listen 8080;\n  resolver __DNS__ valid=10s ipv6=off;\n  client_max_body_size 80m;\n`+
 `  proxy_set_header Host $host;\n  proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;\n`+
 `  proxy_set_header X-Forwarded-Proto $scheme;\n  proxy_http_version 1.1;\n`+
 `  location = /health/gateway { return 200 'ok'; }\n`;
 const config=routes.map(([pattern,service])=>{
  // SSE must never be buffered by Nginx: each committed YugabyteDB event
  // should reach the browser immediately rather than waiting for a full buffer.
  const streaming=pattern.endsWith('solution-events$');
  const proxyHeaders=streaming
    ? '    proxy_set_header Connection "";\n    proxy_buffering off;\n    proxy_cache off;'
    : '    proxy_set_header Upgrade $http_upgrade;\n    proxy_set_header Connection \'upgrade\';';
  return `  location ~* ${pattern} {\n    set $tf_backend '${service}.${LOCAL_NAMESPACE}.svc.cluster.local:8080';\n    proxy_pass http://$tf_backend;\n${proxyHeaders}\n    proxy_read_timeout 180s;\n  }`;
 }).join('\n');
 const hubs=`  location ^~ /hubs/course-map {\n    set $tf_backend 'education-api.${LOCAL_NAMESPACE}.svc.cluster.local:8080';\n    proxy_pass http://$tf_backend;\n    proxy_set_header Upgrade $http_upgrade;\n    proxy_set_header Connection 'upgrade';\n    proxy_read_timeout 300s;\n  }\n  location ^~ /hubs/ {\n    set $tf_backend 'tasks-api.${LOCAL_NAMESPACE}.svc.cluster.local:8080';\n    proxy_pass http://$tf_backend;\n    proxy_set_header Upgrade $http_upgrade;\n    proxy_set_header Connection 'upgrade';\n    proxy_read_timeout 300s;\n  }\n`;
 // Route only the lab's public S3 bucket through this loopback-only gateway.
 // Presigned S3 URLs include host and port in their signature; forward $http_host,
 // not $host, so the local port :18080 survives to MinIO.
 const bucket=`  location ^~ /taskforge-k3s-lab-files/ {\n    set $tf_backend 'minio.${LOCAL_NAMESPACE}.svc.cluster.local:9000';\n    proxy_pass http://$tf_backend;\n    proxy_set_header Host $http_host;\n    proxy_request_buffering off;\n    proxy_read_timeout 300s;\n  }\n`;
 const catchAll=`  location /api/ { return 404; }\n  location / {\n    set $tf_backend 'front.${LOCAL_NAMESPACE}.svc.cluster.local:80';\n    proxy_pass http://$tf_backend;\n  }\n}`;
 return base+config+'\n'+hubs+bucket+catchAll+'\n';
}

export function buildLocalManifests(profile='core'){
 if(!['core','all-api'].includes(profile))throw new Error(`Unknown profile ${profile}`);
 const services=profile==='core'?DATABASE_SERVICES.filter(s=>CORE_NAMES.includes(s.name)):DATABASE_SERVICES;
 const opts={namespace:LOCAL_NAMESPACE,replicas:1,release:LOCAL_RELEASE,imagePrefix:LOCAL_IMAGE_PREFIX,imageTag:LOCAL_IMAGE_TAG,imagePullSecret:''};
 const all=render(opts);
 const jobs=all.jobs.filter(j=>services.some(s=>j.metadata.name===`migrate-${s.name}-${LOCAL_RELEASE}`));
 const apps=all.apps.filter(x=>services.some(s=>x.metadata.name===s.name));
 for(const obj of [...jobs,...apps]){
  if(obj.kind==='Job')obj.spec.template.spec.containers[0].imagePullPolicy='Never';
  if(obj.kind==='Deployment'){
    const c=obj.spec.template.spec.containers[0];c.imagePullPolicy='Never';
    c.resources={requests:{cpu:'50m',memory:'128Mi'},limits:{memory:'768Mi'}};
  }
 }
 const ns={apiVersion:'v1',kind:'Namespace',metadata:{name:LOCAL_NAMESPACE,labels:{'taskforge.io/use':'k3s-isolated-lab'}}};
 const isolation={apiVersion:'networking.k8s.io/v1',kind:'NetworkPolicy',
  metadata:{namespace:LOCAL_NAMESPACE,name:'sandbox-no-external-egress'},
  spec:{podSelector:{},policyTypes:['Egress'],egress:[
    {to:[{podSelector:{}}]},
    {to:[{namespaceSelector:{matchLabels:{'kubernetes.io/metadata.name':'taskforge-k3s-lab-db'}}}],ports:[{protocol:'TCP',port:5433}]},
    {to:[{namespaceSelector:{matchLabels:{'kubernetes.io/metadata.name':'kube-system'}}}],ports:[{protocol:'UDP',port:53},{protocol:'TCP',port:53}]}
  ]}
 };
 const infra=[ns,isolation,configMap('taskforge-app-config',configEnv),
  pvc('redis','2Gi'),pvc('rabbitmq','2Gi'),pvc('minio','5Gi'),
  dep('redis','redis:7.4-alpine',6379,{mount:'/data',memory:'256Mi',command:['sh','-ec'],args:['exec redis-server --appendonly yes --requirepass "$REDIS_PASSWORD"'],env:[envSecret('REDIS_PASSWORD','REDIS_PASSWORD')]}),
  svc('redis',6379),
  dep('rabbitmq','rabbitmq:3.13-management-alpine',5672,{mount:'/var/lib/rabbitmq',memory:'512Mi',env:[{name:'RABBITMQ_DEFAULT_USER',value:'taskforge'},envSecret('RABBITMQ_DEFAULT_PASS','RABBITMQ_PASSWORD')]}),
  svc('rabbitmq',5672,5672,[{name:'management',port:15672,targetPort:15672}]),
  dep('minio','minio/minio:RELEASE.2025-04-22T22-12-26Z',9000,{mount:'/data',memory:'512Mi',command:['minio'],args:['server','/data','--console-address',':9001'],env:[{name:'MINIO_ROOT_USER',value:'taskforge'},envSecret('MINIO_ROOT_PASSWORD','MINIO_ROOT_PASSWORD')],ready:'/minio/health/live'}),
  svc('minio',9000,9000,[{name:'console',port:9001,targetPort:9001}]),
 ];
 const web=[
  configMap('local-gateway-config',{'site.conf':gatewayConfig()}),
  dep('front',`${LOCAL_IMAGE_PREFIX}/front:${LOCAL_IMAGE_TAG}`,80,{local:true,ready:'/health',memory:'384Mi'}),
  svc('front',80),
  dep('local-gateway','nginx:1.27-alpine',8080,{config:true,ready:'/health/gateway',memory:'128Mi',command:['/bin/sh','-ec'],args:[`DNS="$(awk '/^nameserver/{print $2;exit}' /etc/resolv.conf)"; test -n "$DNS"; sed "s/__DNS__/$DNS/g" /etc/nginx/taskforge-lab/site.conf >/etc/nginx/conf.d/default.conf; exec nginx -g 'daemon off;'`]}),
  svc('local-gateway',8080),
 ];
 return{profile,infra,jobs,apps,web,dbNames:services.map(s=>s.database)};
}

export function writeLocalFiles(dir,profile='core'){
 const files=buildLocalManifests(profile);
 if(fs.existsSync(dir)&&fs.readdirSync(dir).length)throw new Error('Refusing to overwrite nonempty output directory: '+dir);
 fs.mkdirSync(dir,{recursive:true});
 for(const [name,value] of Object.entries({'01-infra.json.yaml':files.infra,'02-migrations.json.yaml':files.jobs,'03-apis.json.yaml':files.apps,'04-web.json.yaml':files.web}))
  fs.writeFileSync(path.join(dir,name),toYaml(value));
 fs.writeFileSync(path.join(dir,'inventory.json'),JSON.stringify({profile,databases:files.dbNames,images:[...files.apps.filter(x=>x.kind==='Deployment').map(x=>x.spec.template.spec.containers[0].image),`${LOCAL_IMAGE_PREFIX}/front:${LOCAL_IMAGE_TAG}`]},null,2)+'\n');
 return files;
}
if(process.argv[1]&&path.resolve(process.argv[1])===fileURLToPath(import.meta.url)){
 try{
  const args=process.argv.slice(2);if(args.length!==2||!['core','all-api'].includes(args[0]))throw new Error('Usage: node generate.mjs core|all-api OUTPUT_EMPTY_DIR');
  const f=writeLocalFiles(path.resolve(args[1]),args[0]);
  console.log(`LOCAL K3S: ${f.profile}; ${f.jobs.length} DB migration Jobs, ${f.apps.filter(x=>x.kind==='Deployment').length} API Deployments, real front + gateway, Redis/RabbitMQ/MinIO.`);
 }catch(e){console.error('ERROR:',e.message);process.exitCode=2;}
}
