FROM python:3.13-slim

WORKDIR /app

# Устанавливаем Free Pascal и зависимости
RUN apt-get update \
 && apt-get install -y --no-install-recommends fpc \
 && rm -rf /var/lib/apt/lists/*

RUN pip install --no-cache-dir --break-system-packages fastapi uvicorn

COPY server.py /app/server.py

EXPOSE 8080
CMD ["uvicorn", "server:app", "--host", "0.0.0.0", "--port", "8080"]
