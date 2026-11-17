// Kubernetes JSON is valid YAML; produce reviewable Kubernetes resources without
// embedding passwords, and keep source DB ownership unchanged during cutover.
export const DATABASE_SERVICES = Object.freeze([
  {name:'identity-api',database:'taskforge_identity',connection:'DefaultConnection'},
  {name:'education-api',database:'taskforge_education',connection:'DefaultConnection'},
  {name:'content-api',database:'taskforge_content',connection:'LearningConnection'},
  {name:'tasks-api',database:'taskforge_tasks',connection:'DefaultConnection'},
  {name:'solutions-api',database:'taskforge_solutions',connection:'DefaultConnection'},
  {name:'execution-api',database:'taskforge_execution',connection:'DefaultConnection'},
  {name:'ai-api',database:'taskforge_ai',connection:'DefaultConnection'}, // deterministic account intelligence only
  {name:'support-api',database:'taskforge_support',connection:'DefaultConnection'},
  {name:'minecraft-api',database:'taskforge_minecraft',connection:'DefaultConnection'},
  {name:'files-api',database:'taskforge_files',connection:'DefaultConnection'},
  {name:'notifications-api',database:'taskforge_notifications',connection:'DefaultConnection'},
  {name:'observability-api',database:'taskforge_observability',connection:'DefaultConnection'},
]);
export const BACKGROUND_DB_CONSUMERS = Object.freeze([
  {name:'rating-worker',database:'taskforge_solutions',connection:'DefaultConnection',migrate:false},
  {name:'telegram-quiz-bot',database:'taskforge_telegram_quiz',connection:'TelegramQuizDb',migrate:true},
]); // Telegram bot is preserved; only the retired CT quiz-api was removed.
export const EXTRA_DATABASES = Object.freeze(['taskforge_telegram_quiz']);

const boolEnv=(name,value)=>({name,value:String(value).toLowerCase()});
const configEnv=(service,migration)=>[
  {name:`ConnectionStrings__${service.connection}`,valueFrom:{secretKeyRef:{name:'taskforge-db-connections',key:service.name}}},
  boolEnv('Database__RequireYugabyte',true),
  boolEnv('Database__MigrateOnStartup',migration),
  boolEnv('Database__MigrationJobOnly',migration),
  boolEnv('Database__EnsureCreated',false),
  boolEnv('Cluster__MultiNode',true),
  ...(migration?[boolEnv('Seed__InitialCatalog',false)]:[]),
  ...(service.name==='telegram-quiz-bot'?[boolEnv('TelegramQuiz__ApplyMigrationsOnStartup',false)]:[]),
];

function podContainer(service,options,migration){
  return {
    name:service.name,
    image:`${options.imagePrefix}/${service.name}:${options.imageTag}`,
    imagePullPolicy:'IfNotPresent',
    envFrom:[
      {configMapRef:{name:'taskforge-app-config'}},
      {secretRef:{name:'taskforge-app-secrets'}},
    ],
    env:configEnv(service,migration),
    ...(migration?{}:{
      ports:[{name:'http',containerPort:8080}],
      readinessProbe:{tcpSocket:{port:'http'},periodSeconds:5,timeoutSeconds:2},
      startupProbe:{tcpSocket:{port:'http'},failureThreshold:30,periodSeconds:5},
      resources:{requests:{cpu:'100m',memory:'256Mi'},limits:{memory:'1Gi'}},
    }),
    securityContext:{allowPrivilegeEscalation:false,capabilities:{drop:['ALL']}},
  };
}
function podSpec(service,options,migration){
  return {
    restartPolicy:migration?'Never':'Always',
    ...(options.imagePullSecret?{imagePullSecrets:[{name:options.imagePullSecret}]}:{}),
    containers:[podContainer(service,options,migration)],
  };
}
function labels(service,options){return {app:service.name,'taskforge.io/release':options.release};}
export function makeMigrationJob(service,options){
  const name=`migrate-${service.name}-${options.release}`;
  return {
    apiVersion:'batch/v1',kind:'Job',
    metadata:{name,namespace:options.namespace,labels:labels(service,options)},
    spec:{backoffLimit:0,template:{metadata:{labels:{app:service.name,'taskforge.io/role':'migration'}},spec:podSpec(service,options,true)}},
  };
}
export function makeDeployment(service,options){
  return {
    apiVersion:'apps/v1',kind:'Deployment',
    metadata:{name:service.name,namespace:options.namespace,labels:labels(service,options)},
    spec:{replicas:options.replicas,revisionHistoryLimit:3,
      selector:{matchLabels:{app:service.name}},
      strategy:{type:'RollingUpdate',rollingUpdate:{maxUnavailable:0,maxSurge:1}},
      template:{metadata:{labels:labels(service,options)},spec:podSpec(service,options,false)}},
  };
}
export function makeWorkerDeployment(service,options){
  const pod=podSpec(service,options,false);
  // Only one bot poller and one rating projection worker until leader election is added.
  delete pod.containers[0].ports;
  delete pod.containers[0].readinessProbe;
  delete pod.containers[0].startupProbe;
  return {apiVersion:'apps/v1',kind:'Deployment',
    metadata:{name:service.name,namespace:options.namespace,labels:labels(service,options)},
    spec:{replicas:1,revisionHistoryLimit:3,
      selector:{matchLabels:{app:service.name}},strategy:{type:'Recreate'},
      template:{metadata:{labels:labels(service,options)},spec:pod}}};
}
export function makeService(service,options){
  return {apiVersion:'v1',kind:'Service',metadata:{name:service.name,namespace:options.namespace},
    spec:{type:'ClusterIP',selector:{app:service.name},ports:[{name:'http',port:8080,targetPort:'http'}]}};
}
export function validateOptions(options){
  if(!/^ghcr\.io\/[a-z0-9][a-z0-9._/-]{1,180}$/.test(options.imagePrefix)||options.imagePrefix.includes('..')||options.imagePrefix.endsWith('/'))
    throw new Error('imagePrefix must be a lowercase GHCR image repository prefix');
  if(!/^[a-zA-Z0-9_][a-zA-Z0-9_.-]{5,100}$/.test(options.imageTag)||['latest','develop','main'].includes(options.imageTag))
    throw new Error('imageTag must be an immutable version or SHA, not latest/develop/main');
  if(!/^[a-z0-9]([a-z0-9-]{0,22}[a-z0-9])?$/.test(options.release))
    throw new Error('release must be a short lowercase DNS-compatible identifier');
  if(!/^[a-z0-9]([a-z0-9-]{0,45}[a-z0-9])?$/.test(options.namespace))
    throw new Error('namespace must be DNS-compatible');
  if(!Number.isInteger(options.replicas)||options.replicas<1||options.replicas>20)
    throw new Error('replicas must be in [1,20]');
  if(options.imagePullSecret&&!/^[a-z0-9][a-z0-9.-]{0,61}[a-z0-9]$/.test(options.imagePullSecret))
    throw new Error('imagePullSecret invalid');
}
export function render(options){
  validateOptions(options);
  const jobs=[...DATABASE_SERVICES,...BACKGROUND_DB_CONSUMERS.filter(s=>s.migrate)].map(s=>makeMigrationJob(s,options));
  const apps=[...DATABASE_SERVICES.flatMap(s=>[makeDeployment(s,options),makeService(s,options)]),
    ...BACKGROUND_DB_CONSUMERS.map(s=>makeWorkerDeployment(s,options))];
  return {jobs,apps};
}
export const toYaml=(resources)=>resources.map(r=>JSON.stringify(r,null,2)).join('\n---\n')+'\n';
