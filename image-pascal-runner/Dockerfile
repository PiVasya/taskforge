FROM debian:bookworm-slim

RUN apt-get update \
  && apt-get install -y --no-install-recommends \
    fp-compiler ca-certificates \
    python3 python3-pip \
  && rm -rf /var/lib/apt/lists/*

WORKDIR /app

# We'll use a tiny FastAPI wrapper for HTTP.
COPY requirements.txt ./
RUN pip3 install --no-cache-dir -r requirements.txt

COPY app ./app

EXPOSE 8000

CMD ["python3", "-m", "uvicorn", "app.main:app", "--host", "0.0.0.0", "--port", "8000"]