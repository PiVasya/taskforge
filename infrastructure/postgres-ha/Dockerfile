FROM postgres:18-bookworm

ARG PATRONI_VERSION=4.1.5

RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates curl python3 python3-venv \
    && rm -rf /var/lib/apt/lists/* \
    && python3 -m venv /opt/patroni \
    && /opt/patroni/bin/pip install --no-cache-dir --upgrade pip setuptools wheel \
    && /opt/patroni/bin/pip install --no-cache-dir "patroni[etcd3]==${PATRONI_VERSION}" "psycopg[binary]>=3.2,<4"

COPY infrastructure/postgres-ha/entrypoint.sh /usr/local/bin/taskforge-patroni-entrypoint
RUN chmod 0755 /usr/local/bin/taskforge-patroni-entrypoint

EXPOSE 5432 8008
ENTRYPOINT ["/usr/local/bin/taskforge-patroni-entrypoint"]
CMD ["/run/taskforge-cluster/patroni.yml"]
