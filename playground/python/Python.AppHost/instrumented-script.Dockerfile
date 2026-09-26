FROM python:3.13-slim-bookworm

# ------------------------------
# 🚀 Python Application
# ------------------------------
# Create non-root user for security
RUN groupadd --system --gid 999 appuser && useradd --system --gid 999 --uid 999 --create-home appuser

# Set working directory
WORKDIR /app

# Copy requirements.txt for dependency installation
COPY requirements.txt /app/requirements.txt

# Install dependencies using pip
RUN apt-get update \
  && apt-get install -y --no-install-recommends build-essential \
  && pip install --no-cache-dir -r requirements.txt \
  && apt-get purge -y --auto-remove build-essential \
  && rm -rf /var/lib/apt/lists/*

# Copy application files
COPY --chown=appuser:appuser . /app

# Set environment variables
ENV PYTHONDONTWRITEBYTECODE=1
ENV PYTHONUNBUFFERED=1

# Use the non-root user to run the application
USER appuser

# Run the application
ENTRYPOINT ["python","main.py"]
