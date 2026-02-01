FROM debian:bookworm-slim

# PascalABC.NET (console compiler) + Mono + headless X (Xvfb) + screenshot (ImageMagick)
RUN apt-get update \
  && apt-get install -y --no-install-recommends \
    ca-certificates wget curl unzip \
    python3 python3-pip \
    mono-complete libgdiplus \
    xvfb xauth xdotool \
    imagemagick \
    fonts-dejavu-core \
  && rm -rf /var/lib/apt/lists/*

# Install PascalABC.NET console compiler (pabcnetc.exe)
# Official download link: https://pascalabc.net/downloads/PABCNETC.zip
RUN mkdir -p /opt/pabcnetc \
  # Some environments (e.g. GitHub Actions) get 403 on direct downloads unless a browser-like User-Agent/Referer is set. \
  && (curl -fL --retry 3 --retry-all-errors \
        -A "Mozilla/5.0" \
        -e "https://pascalabc.net/ssyilki-dlya-skachivaniya" \
        -o /tmp/PABCNETC.zip \
        https://pascalabc.net/downloads/PABCNETC.zip \
      || curl -fL --retry 3 --retry-all-errors \
        -A "Mozilla/5.0" \
        -e "https://pascalabc.net/ssyilki-dlya-skachivaniya" \
        -o /tmp/PABCNETC.zip \
        http://pascalabc.net/downloads/PABCNETC.zip \
      || wget --tries=3 --timeout=30 \
        --user-agent="Mozilla/5.0" \
        --referer="https://pascalabc.net/ssyilki-dlya-skachivaniya" \
        -O /tmp/PABCNETC.zip \
        https://pascalabc.net/downloads/PABCNETC.zip) \
  # PABCNETC.zip contains some files with non-UTF filenames; Info-ZIP 'unzip' may exit with code 1 \
  # while still extracting everything ("mismatching local filename" warning). \
  && (unzip -q /tmp/PABCNETC.zip -d /opt/pabcnetc || true) \
  # Ensure the compiler is actually present so the build fails only when extraction truly failed. \
  && test -n "$(find /opt/pabcnetc -maxdepth 6 -iname 'pabcnetc.exe' -print -quit)" \
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
