# Kubernetes layout

Не растягивать один Kubernetes cluster на РБ/Израиль/Польшу.

Использовать отдельные overlays:

```text
infrastructure/k8s/overlays/rb
infrastructure/k8s/overlays/israel
infrastructure/k8s/overlays/poland
```

Primary data-plane на первом этапе лучше держать в одном регионе:

```text
Poland: PostgreSQL primary, RabbitMQ primary, MinIO primary
Israel: API/worker/runner replicas + standby DB
RB: API/worker/runner replicas + optional standby DB
```
