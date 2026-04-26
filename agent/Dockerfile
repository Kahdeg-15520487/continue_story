FROM node:22-alpine
WORKDIR /app
RUN npm install -g @mariozechner/pi-coding-agent
COPY package.json ./
RUN npm install
COPY src/ ./src/

# Use existing 'node' user (uid 1000) — same as api container's appuser
RUN mkdir -p /home/node/.pi/agent && chown -R node:node /home/node/.pi

USER node
EXPOSE 3001
CMD ["npx", "tsx", "src/index.ts"]
