FROM debian:bookworm-slim

# PascalABC.NET (console compiler) + Mono + headless X (Xvfb) + screenshot (ImageMagick)
RUN apt-get update \
  && apt-get install -y --no-install-recommends \
    ca-certificates wget unzip \
    python3 python3-pip \
    mono-complete libgdiplus \
    xvfb xauth \
    imagemagick \
    fonts-dejavu-core \
  && rm -rf /var/lib/apt/lists/*

# Install PascalABC.NET console compiler (pabcnetc.exe)
# Official download link: https://pascalabc.net/downloads/PABCNETC.zip
RUN mkdir -p /opt/pabcnetc \
  && wget -O /tmp/PABCNETC.zip https://pascalabc.net/downloads/PABCNETC.zip \
  && unzip -q /tmp/PABCNETC.zip -d /opt/pabcnetc \
  && rm -f /tmp/PABCNETC.zip

# Some Mono apps expect a writable registry path inside containers
ENV MONO_REGISTRY_PATH=/tmp/mono-registry
RUN mkdir -p /tmp/mono-registry/LocalMachine

WORKDIR /app

COPY requirements.txt ./
# Use --break-system-packages to bypass Debian's PEP 668 restriction for pip
RUN pip3 install --no-cache-dir --break-system-packages -r requirements.txt

COPY app ./app

EXPOSE 8000

CMD ["python3", "-m", "uvicorn", "app.main:app", "--host", "0.0.0.0", "--port", "8000"]
