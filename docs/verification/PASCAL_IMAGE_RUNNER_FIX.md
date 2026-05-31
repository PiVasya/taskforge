# Pascal image runner Dockerfile fix

## Problem

Docker build for `image-pascal-runner` failed after `PABCNETC.zip` extraction with:

```text
ln: '/opt/pabcnetc/pabcnetc.exe' and '/opt/pabcnetc/pabcnetc.exe' are the same file
```

The extractor sometimes places `pabcnetc.exe` directly at `/opt/pabcnetc/pabcnetc.exe`, so the previous unconditional symlink command attempted to link the file to itself and returned exit code 1.

## Fix

The Dockerfile now creates the symlink only when the discovered file path differs from the canonical target:

```sh
target="/opt/pabcnetc/pabcnetc.exe"
if [ "$found" != "$target" ]; then ln -sfn "$found" "$target"; fi
test -f "$target"
```

## Notes

No EF migrations were generated.
