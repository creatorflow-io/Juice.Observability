# A small non-.NET service for the Phase 12 proof (R8): Python and Flask with the stock OpenTelemetry SDK. Nothing from Juice.
FROM python:3.13-slim
WORKDIR /app
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt
COPY app.py entrypoint.sh ./
RUN chmod +x entrypoint.sh && useradd --system --uid 10001 sample
USER 10001
EXPOSE 8080
ENTRYPOINT ["/app/entrypoint.sh"]
