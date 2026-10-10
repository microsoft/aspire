FROM node:24-bookworm-slim
WORKDIR /app
COPY .output/ .output/
USER node
ENV HOST=0.0.0.0 PORT=3000
EXPOSE 3000
CMD ["node", ".output/server/index.mjs"]
