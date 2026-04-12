FROM python:3.13-slim

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    PYTHONNOUSERSITE=1

RUN useradd -r -u 10001 -g root sandbox
USER sandbox
WORKDIR /workspace

CMD ["python", "-V"]
