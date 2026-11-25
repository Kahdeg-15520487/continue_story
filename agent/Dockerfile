FROM node:22.19-alpine
WORKDIR /app
# pi-durable requires Node >= 22.19 (node:sqlite). The lock file is copied so
# builds are reproducible; the pi-durable suite is pinned exactly (experimental API).
COPY package.json package-lock.json ./
RUN npm ci
COPY src/ ./src/

# Use existing 'node' user (uid 1000) — same as api container's appuser
USER node
EXPOSE 3001
CMD ["npx", "tsx", "src/index.ts"]
