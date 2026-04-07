FROM mcr.microsoft.com/devcontainers/dotnet:10.0-noble

# Workaround for expired Yarn GPG key (https://github.com/yarnpkg/yarn/issues/9218)
RUN rm -f /etc/apt/sources.list.d/yarn.list 2>/dev/null || true \
    && rm -f /usr/share/keyrings/yarn-archive-keyring.gpg 2>/dev/null || true \
    && rm -f /usr/share/keyrings/yarn.gpg 2>/dev/null || true
