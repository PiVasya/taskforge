# java-runner/Dockerfile
FROM python:3.11-slim

# Устанавливаем JDK
RUN apt-get update \
    && apt-get install -y --no-install-recommends openjdk-17-jdk \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY server.py /app/server.py

# Устанавливаем FastAPI и зависимости
RUN pip install --no-cache-dir fastapi uvicorn pydantic

EXPOSE 8080
CMD ["uvicorn", "server:app", "--host", "0.0.0.0", "--port", "8080"]
