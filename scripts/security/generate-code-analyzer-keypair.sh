#!/bin/sh
set -eu

output_dir="${1:-.runtime/code-analyzer-keys}"
private_key="$output_dir/code-analyzer-private.pem"
public_key="$output_dir/code-analyzer-public.pem"

umask 077
mkdir -p "$output_dir"

tmp_private="$private_key.tmp.$$"
tmp_public="$public_key.tmp.$$"
trap 'rm -f "$tmp_private" "$tmp_public"' EXIT HUP INT TERM

openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:3072 -out "$tmp_private"
openssl pkey -in "$tmp_private" -pubout -out "$tmp_public"
openssl pkey -in "$tmp_private" -check -noout >/dev/null
openssl pkey -pubin -in "$tmp_public" -text -noout | grep -Eq 'Public-Key: \((3072|[4-9][0-9]{3,}) bit\)'

chmod 0600 "$tmp_private"
chmod 0644 "$tmp_public"
mv -f "$tmp_private" "$private_key"
mv -f "$tmp_public" "$public_key"
trap - EXIT HUP INT TERM

printf 'Private key: %s\nPublic key:  %s\n' "$private_key" "$public_key"
