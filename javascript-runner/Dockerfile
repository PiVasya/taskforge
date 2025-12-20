FROM python:3.13-slim

# Install Node.js and npm; remove package lists to keep the image slim
RUN apt-get update \
    && apt-get install -y --no-install-recommends nodejs npm \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app

# Install minimal Python dependencies for FastAPI
RUN pip install --no-cache-dir fastapi uvicorn

COPY server.py /app/server.py

EXPOSE 8080
CMD ["uvicorn", "server:app", "--host", "0.0.0.0", "--port", "8080"]
