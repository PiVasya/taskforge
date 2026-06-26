FROM python:3.11-slim

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    PIP_NO_CACHE_DIR=1 \
    OPENCLIP_CACHE_DIR=/models/openclip \
    HF_HOME=/models/openclip \
    HUGGINGFACE_HUB_CACHE=/models/openclip \
    HF_HUB_CACHE=/models/openclip \
    HF_HUB_DISABLE_XET=1 \
    HOME=/tmp

WORKDIR /app

# Minimal deps. Pillow wheels are bundled, but keep common runtime libs.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
        ca-certificates \
        wget \
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

RUN python scripts/download_models.py \
    && chown -R 65534:65534 /models

EXPOSE 8080

ARG TASKFORGE_DEBUG_LOGS=0
ENV TASKFORGE_DEBUG_LOGS=${TASKFORGE_DEBUG_LOGS}
USER 65534:65534
CMD ["sh", "-c", "if [ \"${TASKFORGE_DEBUG_LOGS:-0}\" = \"1\" ]; then echo \"[taskforge-debug] logs=on service=image-analyzer\"; exec uvicorn app.main:app --host 0.0.0.0 --port 8080 --log-level debug; else echo \"[taskforge-debug] logs=off service=image-analyzer\"; exec uvicorn app.main:app --host 0.0.0.0 --port 8080 --log-level info; fi"]
