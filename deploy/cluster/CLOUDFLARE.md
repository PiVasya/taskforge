# Cloudflare

Сгенерировать список origins:

```bash
python3 ./cluster/manager.py --inventory ./cluster/inventory.json cloudflare
```

Для каждого узла задаётся его собственный HTTPS destination port. Поэтому C может слушать `8443`, даже если A/B используют `443`.

До включения quorum-mode B/C не должны считаться автоматически writable только по HTTP health. После Patroni/etcd endpoint `/ha/traffic-ready` возвращает 200 только на текущем безопасном primary.
