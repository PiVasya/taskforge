# DEPRECATED: legacy Python AI worker. Use taskforge-ai-agent-dotnet for new development.
FROM python:3.12-slim

WORKDIR /app
RUN mkdir -p /app/logs
ENV TASKFORGE_AI_LOG_DIR=/app/logs
COPY requirements.txt ./
RUN pip install --no-cache-dir -r requirements.txt
COPY . ./
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 CMD ["python", "preflight.py", "--healthcheck"]
CMD ["python", "worker.py"]
