FROM python:3.12-alpine
RUN pip install --no-cache-dir paho-mqtt==2.1.0 PyYAML==6.0.2
WORKDIR /app
COPY relay.py prepare_config.py ./
