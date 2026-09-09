#!/usr/bin/env python3
"""Prepare local SQL-only secrets and pin actual Docker images. No DB or app mutations.

The file is preserved on upgrades. Each node has its own secrets and disposable cache.
--pull resolves a new installation once, never silently updates an existing image pin.
"""
from __future__ import annotations
import argparse
import json
import os
from pathlib import Path
import re
import secrets
import subprocess
import tempfile


def read_env(path):
    out={}
    if path.is_file():
        for raw in path.read_text().splitlines():
            s=raw.strip()
            if not s or s.startswith('#') or '=' not in s:continue
            k,v=s.split('=',1);out[k]=v.strip().strip('\"\'')
    return out


def pin(image, pull):
    if not re.fullmatch(r'[a-zA-Z0-9./_:@-]+', image):raise ValueError('Invalid SQL engine image name')
    if pull:
        subprocess.run(['docker','pull',image],check=True)
    info=json.loads(subprocess.check_output(['docker','image','inspect',image],text=True))[0]
    digests=info.get('RepoDigests') or []
    if not digests:raise ValueError('SQL engine image has no registry digest; pull the official image first')
    selected=next((d for d in digests if '@' in d and (image.split('@')[0].split(':')[0].split('/')[-1] in d.split('@')[0].split('/')[-1])),digests[0])
    digest=selected.split('@')[-1]
    if not re.fullmatch(r'sha256:[a-f0-9]{64}',digest):raise ValueError('Invalid Docker RepoDigest')
    if '@sha256:' in image and image.split('@')[-1]!=digest:
        selected=next((d for d in digests if d.endswith('@'+image.split('@')[-1])),None)
        if selected is None:raise ValueError('Pulled SQL image does not match its saved pin')
        digest=selected.split('@')[-1]
    return selected,digest


def write_atomic(path, values, owner):
    path.parent.mkdir(parents=True,exist_ok=True)
    fd,tmp=tempfile.mkstemp(prefix='.sql-runtime-',dir=path.parent)
    try:
        os.fchmod(fd,0o600)
        if os.geteuid()==0 and owner and owner.is_file():
            st=owner.stat();os.fchown(fd,st.st_uid,st.st_gid)
        with os.fdopen(fd,'w') as f:
            f.write('# Local SQL execution cache only; never production database credentials.\n')
            for k,v in sorted(values.items()):
                if not re.fullmatch(r'[a-zA-Z0-9_./:@+-]+',str(v)):raise ValueError('Unsafe environment value: '+k)
                f.write(k+'='+str(v)+'\n')
            f.flush();os.fsync(f.fileno())
        os.replace(tmp,path)
    finally:
        if os.path.exists(tmp):os.unlink(tmp)


def main():
    ap=argparse.ArgumentParser(description=__doc__)
    ap.add_argument('--env-file',required=True,type=Path)
    ap.add_argument('--base-env',type=Path)
    ap.add_argument('--node',default='dev')
    ap.add_argument('--profile',choices=['full','lite','none'],default='full')
    ap.add_argument('--pull',action='store_true')
    ap.add_argument('--check',action='store_true')
    args=ap.parse_args()
    values=read_env(args.env_file)
    base=read_env(args.base_env) if args.base_env else {}
    if args.profile!='full' or base.get('SQL_ENABLED','true').lower() in {'0','false','no'}:
        if not args.check:write_atomic(args.env_file,{'SQL_ENABLED':'false','SQL_WORKER_ID':'sql-'+args.node},args.base_env)
        print('SQL execution disabled for profile='+args.profile);return
    if args.check:
        for k in ('SQL_SANDBOX_MARKER','SQL_POSTGRES_PASSWORD','SQL_MYSQL_PASSWORD'):
            if not re.fullmatch('[a-f0-9]{64}',values.get(k,'')):raise ValueError('SQL runtime not prepared: '+k)
        for engine in ('POSTGRES','MYSQL'):
            if not re.fullmatch(r'sha256:[a-f0-9]{64}',values.get('SQL_'+engine+'_RUNTIME_DIGEST','')):raise ValueError('SQL image pin missing')
        print('SQL local secrets and image pins present (no network/database checks)');return
    values['SQL_ENABLED']='true';values['SQL_WORKER_ID']='sql-'+args.node
    for k in ('SQL_SANDBOX_MARKER','SQL_POSTGRES_PASSWORD','SQL_MYSQL_PASSWORD'):
        if not values.get(k):values[k]=secrets.token_hex(32)
        if not re.fullmatch('[a-f0-9]{64}',values[k]):raise ValueError('Refusing to replace malformed existing SQL secret: '+k)
    for engine,default in [('POSTGRES','postgres:18-bookworm'),('MYSQL','mysql:8.4')]:
        key='SQL_'+engine+'_IMAGE';saved=values.get(key)
        image=saved or base.get(key) or default
        resolved,digest=pin(image,args.pull)
        values[key]=resolved;values['SQL_'+engine+'_RUNTIME_DIGEST']=digest
        print(engine+' pinned to '+resolved)
    write_atomic(args.env_file,values,args.base_env)
    print('Prepared '+str(args.env_file)+'; secrets not printed; no database/container was started.')


if __name__=='__main__':
    try:main()
    except (ValueError,subprocess.CalledProcessError,OSError) as e:raise SystemExit('SQL preparation failed: '+str(e))
