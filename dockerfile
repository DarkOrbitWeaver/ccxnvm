FROM python:3.10.6-slim-bullseye

# Install COMPILER DEPENDENCIES FIRST
RUN apt-get update && apt-get install -y \
    gcc \
    python3-dev \
    libffi-dev \
    libssl-dev \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app

# Copy requirements FIRST for caching
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

# Then copy application files
COPY . .

CMD ["uvicorn", "server:app", "--host", "0.0.0.0", "--port", "8000"]