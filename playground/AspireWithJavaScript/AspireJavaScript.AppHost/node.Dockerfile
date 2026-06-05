FROM node:22-alpine AS build

WORKDIR /app
COPY package*.json ./
RUN --mount=type=cache,target=/root/.npm npm ci
COPY . .

FROM node:22-alpine AS runtime

WORKDIR /app
COPY --from=build /app /app

ENV NODE_ENV=production

USER node

ENTRYPOINT ["node","app.js"]
