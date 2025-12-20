FROM python:3.13-slim

WORKDIR /app

# Устанавливаем OpenJDK (дефолтную версию) и зависимости
RUN apt-get update \
 && apt-get install -y --no-install-recommends default-jdk \
 && rm -rf /var/lib/apt/lists/*

RUN pip install --no-cache-dir fastapi uvicorn

# Ограничиваем память JVM и размер code cache
ENV JAVA_TOOL_OPTIONS="-Xms16m -Xmx256m -XX:ReservedCodeCacheSize=64m -XX:InitialCodeCacheSize=16m -XX:MaxMetaspaceSize=128m -XX:+UseSerialGC"

COPY server.py /app/server.py

EXPOSE 8080
CMD ["uvicorn", "server:app", "--host", "0.0.0.0", "--port", "8080"]
