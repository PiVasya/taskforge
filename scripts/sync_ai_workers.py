#!/usr/bin/env python3
from pathlib import Path
import shutil

ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / 'taskforge-ai-worker'
DST = ROOT / 'taskforge-ai-worker-external'
FILES = [
    'api_client.py', 'batch_pipeline.py', 'config.py', 'fallbacks.py', 'log.py',
    'mutation.py', 'ollama.py', 'payload.py', 'prompt_builder.py', 'repair.py',
    'reviews.py', 'runners.py', 'text_utils.py', 'validators.py', 'worker.py',
    'tests/test_contracts.py',
]

for rel in FILES:
    src = SRC / rel
    dst = DST / rel
    if not src.exists():
        raise FileNotFoundError(src)
    dst.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(src, dst)
    print(f'synced {rel}')
