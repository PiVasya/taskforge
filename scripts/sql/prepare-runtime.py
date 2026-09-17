#!/usr/bin/env python3
"""Prepare local SQL-only secrets and pin actual Docker images. No DB or app mutations.

The file is preserved on upgrades. Each node has its own secrets and disposable cache.
--pull resolves the selected runtime once; it never silently follows a moving tag.
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

# MySQL 8.4.0 is intentionally represented by two official runtime variants of the
# same server release. Oracle Linux 9 is preferred on x86-64-v2 capable hosts;
# Oracle Linux 8 is the baseline-compatible variant for older/masked x86-64 CPUs.
# The Tasks API explicitly certifies only this immutable pair as mutually compatible.
MYSQL_PREFERRED_DIGEST = 'sha256:dab7049abafe3a0e12cbe5e49050cf149881c0cd9665c289e5808b9dad39c9e0'
MYSQL_CPUV1_DIGEST = 'sha256:f7a8e140a7d6d1e6e0c99eeb0489c50a186ee4ac44ff55323a176529b9a43d33'
MYSQL_PREFERRED_IMAGE = 'mysql@' + MYSQL_PREFERRED_DIGEST
MYSQL_CPUV1_IMAGE = 'mysql@' + MYSQL_CPUV1_DIGEST
MYSQL_CERTIFIED_DIGESTS = frozenset({MYSQL_PREFERRED_DIGEST, MYSQL_CPUV1_DIGEST})
MYSQL_RUNTIME_FAMILY = 'mysql-8.4.0-ol8-ol9-v1'
_X86_64_V2_FLAGS = ('cx16', 'lahf_lm', 'popcnt', 'pni', 'ssse3', 'sse4_1', 'sse4_2')


def read_env(path):
    out={}
    if path.is_file():
        for raw in path.read_text().splitlines():
            s=raw.strip()
            if not s or s.startswith('#') or '=' not in s:continue
            k,v=s.split('=',1);out[k]=v.strip().strip('"\'')
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


def host_needs_cpuv1(cpuinfo_path=Path('/proc/cpuinfo')):
    """Match the same conservative x86-64-v2 capability gate used by MinIO."""
    if os.uname().machine != 'x86_64':
        return False
    try:
        flags=''
        for raw in cpuinfo_path.read_text(errors='ignore').splitlines():
            if raw.lower().startswith('flags') and ':' in raw:
                flags=' '+raw.split(':',1)[1].strip()+' '
                break
    except OSError:
        return False
    if not flags.strip():
        return False
    return any(f' {flag} ' not in flags for flag in _X86_64_V2_FLAGS)


def observed_x86_v2_failure(project='taskforge-prod'):
    """A previous fatal glibc message overrides optimistic CPU feature detection."""
    container=f'{project}-sql-mysql-1'
    try:
        result=subprocess.run(['docker','logs','--tail','80',container],text=True,
                              stdout=subprocess.PIPE,stderr=subprocess.STDOUT,timeout=5,check=False)
    except (OSError, subprocess.SubprocessError):
        return False
    return 'CPU does not support x86-64-v2' in (result.stdout or '')


def image_digest(image):
    value=str(image or '').strip()
    return value.split('@',1)[1] if '@' in value else ''


def select_mysql_image(base, values):
    explicit=base.get('SQL_MYSQL_IMAGE')
    if explicit:
        return explicit,'operator-override'
    project=base.get('COMPOSE_PROJECT_NAME') or 'taskforge-prod'
    if observed_x86_v2_failure(project):
        return MYSQL_CPUV1_IMAGE,'observed-x86-64-v2-failure'

    # Like MinIO's cpu.env override, a previously selected certified variant is a
    # stable node-local hardware decision. Correct it only when the current host
    # demonstrably needs the baseline-compatible image.
    existing=values.get('SQL_MYSQL_IMAGE')
    existing_digest=image_digest(existing)
    needs_cpuv1=host_needs_cpuv1()
    if existing_digest in MYSQL_CERTIFIED_DIGESTS:
        if needs_cpuv1 and existing_digest != MYSQL_CPUV1_DIGEST:
            return MYSQL_CPUV1_IMAGE,'corrected-stale-hardware-selection'
        return existing,'stable-hardware-selection'
    if needs_cpuv1:
        return MYSQL_CPUV1_IMAGE,'host-cpu-capability'
    return MYSQL_PREFERRED_IMAGE,'preferred-x86-64-v2-runtime'


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

    postgres_image=values.get('SQL_POSTGRES_IMAGE') or base.get('SQL_POSTGRES_IMAGE') or 'postgres:18-bookworm'
    resolved,digest=pin(postgres_image,args.pull)
    values['SQL_POSTGRES_IMAGE']=resolved;values['SQL_POSTGRES_RUNTIME_DIGEST']=digest
    print('POSTGRES pinned to '+resolved)

    mysql_image,mysql_reason=select_mysql_image(base,values)
    resolved,digest=pin(mysql_image,args.pull)
    values['SQL_MYSQL_IMAGE']=resolved;values['SQL_MYSQL_RUNTIME_DIGEST']=digest
    certified=digest in MYSQL_CERTIFIED_DIGESTS
    family=MYSQL_RUNTIME_FAMILY if certified else 'operator-custom'
    values['SQL_MYSQL_RUNTIME_FAMILY']=family
    values['SQL_MYSQL_RUNTIME_VARIANT']='cpuv1' if digest==MYSQL_CPUV1_DIGEST else ('preferred' if digest==MYSQL_PREFERRED_DIGEST else 'operator')
    print('MYSQL pinned to '+resolved+' reason='+mysql_reason+' family='+family)

    write_atomic(args.env_file,values,args.base_env)
    print('Prepared '+str(args.env_file)+'; secrets not printed; no database/container was started.')


if __name__=='__main__':
    try:main()
    except (ValueError,subprocess.CalledProcessError,OSError) as e:raise SystemExit('SQL preparation failed: '+str(e))
