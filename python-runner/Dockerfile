FROM python:3.12-slim
WORKDIR /app
RUN pip install fastapi uvicorn
COPY server.py ./
EXPOSE 8080
USER 65534
ENTRYPOINT ["uvicorn","server:app","--host","0.0.0.0","--port","8080"]
