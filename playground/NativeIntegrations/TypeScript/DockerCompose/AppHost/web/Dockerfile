# DisableDockerDetector "Playground sample - not a production image"
FROM node:24-alpine
WORKDIR /app
COPY server.mts .
USER node
EXPOSE 8080
ENTRYPOINT ["node"]
CMD ["server.mts"]
