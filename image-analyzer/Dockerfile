FROM python:3.11-slim

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    PIP_NO_CACHE_DIR=1 \
    OPENCLIP_CACHE_DIR=/models/openclip

WORKDIR /app

# Minimal deps. Pillow wheels are bundled, but keep common runtime libs.
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        ca-certificates \
        libgomp1 \
    && rm -rf /var/lib/apt/lists/*

RUN python -m pip install --upgrade pip

# Install CPU PyTorch (kept OUT of requirements.txt so pip can use the extra index)
RUN pip install --extra-index-url https://download.pytorch.org/whl/cpu \
    torch==2.2.2 \
    torchvision==0.17.2

COPY requirements.txt ./requirements.txt
RUN pip install -r requirements.txt

COPY app ./app
COPY scripts ./scripts

# Pre-download model weights during image build (so runtime startup is fast/steady)
ARG MODEL_NAME=ViT-L-14
ARG MODEL_PRETRAINED=laion2b_s32b_b82k
ENV MODEL_NAME=${MODEL_NAME} \
    MODEL_PRETRAINED=${MODEL_PRETRAINED}

RUN python scripts/download_models.py

EXPOSE 8080

CMD ["uvicorn", "app.main:app", "--host", "0.0.0.0", "--port", "8080"]
