# GHCR lowercase image tag fix

GitHub Actions failed after merging microservices into `develop` because Docker image tags were generated from `${{ github.repository }}`.

For this repository the value is `PiVasya/taskforge`, and GHCR/Docker repository names must be lowercase. The workflow therefore produced tags like:

```text
ghcr.io/PiVasya/taskforge/gateway:<sha>
```

Buildx rejected those tags with:

```text
invalid tag ... repository name must be lowercase
```

## Fix

`.github/workflows/develop-build.yml` now prepares a lowercase image prefix in a shell step:

```bash
repository="${GITHUB_REPOSITORY,,}"
echo "prefix=ghcr.io/${repository}" >> "$GITHUB_OUTPUT"
```

All pushed tags now use:

```text
${{ steps.image.outputs.prefix }}/${{ matrix.name }}:<tag>
```

So the generated image names become:

```text
ghcr.io/pivasya/taskforge/gateway:<sha>
ghcr.io/pivasya/taskforge/gateway:develop
```

## Verification

`scripts/verify-structure.sh` now checks that the workflow does not use `${{ github.repository }}` directly inside GHCR tags and that the lowercase prefix step exists.
