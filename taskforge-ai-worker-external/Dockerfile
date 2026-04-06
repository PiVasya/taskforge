FROM python:3.12-slim

WORKDIR /app
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt
COPY config.py log.py text_utils.py api_client.py runners.py \
     payload.py validators.py mutation.py ollama.py \
     prompt_builder.py reviews.py batch_pipeline.py \
     fallbacks.py repair.py worker.py ./
CMD ["python", "worker.py"]
