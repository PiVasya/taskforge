FROM python:3.13-slim

WORKDIR /app

# Устанавливаем OpenJDK (дефолтную версию) и зависимости
RUN apt-get update \
 && apt-get install -y --no-install-recommends default-jdk \
 && rm -rf /var/lib/apt/lists/*

RUN pip install --no-cache-dir fastapi uvicorn

COPY server.py /app/server.py

EXPOSE 8080
CMD ["uvicorn", "server:app", "--host", "0.0.0.0", "--port", "8080"]
