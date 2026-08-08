FROM ghcr.io/astral-sh/uv:python3.13-bookworm-slim AS builder

# Enable bytecode compilation and copy mode for the virtual environment
ENV UV_COMPILE_BYTECODE=1
ENV UV_LINK_MODE=copy

WORKDIR /app

# Copy pyproject.toml to install dependencies
COPY pyproject.toml /app/

# Install dependencies and generate lock file
# Uses BuildKit cache mount to speed up repeated builds
RUN --mount=type=cache,target=/root/.cache/uv \
    uv sync --no-install-project --no-dev

# Copy the rest of the application source and install the project
COPY . /app
RUN --mount=type=cache,target=/root/.cache/uv \
    uv sync --no-dev

FROM python:3.13-slim-bookworm AS app

# ------------------------------
# 🚀 Runtime stage
# ------------------------------
# Create non-root user for security
RUN groupadd --system --gid 999 appuser && useradd --system --gid 999 --uid 999 --create-home appuser

# Copy the application and virtual environment from builder
COPY --from=builder --chown=appuser:appuser /app /app

# Add virtual environment to PATH and set VIRTUAL_ENV
ENV PATH=/app/.venv/bin:${PATH}
ENV VIRTUAL_ENV=/app/.venv
ENV PYTHONDONTWRITEBYTECODE=1
ENV PYTHONUNBUFFERED=1

# Use the non-root user to run the application
USER appuser

# Set working directory
WORKDIR /app

# Run the application
ENTRYPOINT ["python","-m","flask"]
