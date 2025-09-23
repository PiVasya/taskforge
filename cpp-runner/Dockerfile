FROM gcc:latest
RUN apt-get update && apt-get install -y --no-install-recommends python3 python3-venv \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
RUN python3 -m venv .venv && . .venv/bin/activate && pip install fastapi uvicorn
COPY server.py ./
EXPOSE 8080
USER 65534
ENTRYPOINT [".venv/bin/uvicorn","server:app","--host","0.0.0.0","--port","8080"]
