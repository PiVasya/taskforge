FROM debian:bookworm-slim

WORKDIR /app

# Headless rendering for Turtle/Tk:
# - python3-tk provides tkinter module
# - xvfb provides a virtual X server
# - ghostscript converts canvas PostScript to PNG
RUN apt-get update \
  && apt-get install -y --no-install-recommends \
    ca-certificates \
    python3-full python3-venv python3-pip python3-tk \
	    xvfb xauth ghostscript \
    fonts-dejavu \
  && rm -rf /var/lib/apt/lists/*

# Create venv to avoid Debian PEP 668 restrictions.
RUN python3 -m venv /opt/venv
ENV PATH="/opt/venv/bin:$PATH"

COPY requirements.txt ./
RUN pip install --no-cache-dir -r requirements.txt

COPY app ./app

EXPOSE 8000
CMD ["uvicorn", "app.main:app", "--host", "0.0.0.0", "--port", "8000", "--log-level", "info"]
