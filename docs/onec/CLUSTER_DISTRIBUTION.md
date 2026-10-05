# 1C platform distribution in a TaskForge cluster

The application image `onec-runner` intentionally does not contain the
proprietary 1C platform. Starting with the matching server bundle v41.4.0, the
platform installer is seeded once on the current Patroni Primary and then
propagated through each node's private MinIO over WireGuard.

The server control plane stores the installer as verified chunks plus a manifest
under a dedicated MinIO cache prefix. A new node discovers the desired 1C
version, selects up to four complete seed nodes, downloads different chunks in
parallel, verifies every chunk and the full installer SHA-256, and then mirrors
the verified cache into its own MinIO before installing the platform into the
persistent `onec-platform` volume.

A node becomes a seed only after full-file verification. Seed subsets are chosen
per requesting node, so larger clusters distribute upload traffic instead of
always concentrating it on the first server. Repeated migrations reuse both the
local MinIO cache and the reconstructed host installer.

Operationally this means the official/external installer is downloaded once for
the cluster. There is no per-node installer configuration: after the initial
Primary seed, ordinary rolling `migrate` converges every standby and future node.
The long-lived `onec-runner` itself remains on an internal Docker network with no
external Internet access.
