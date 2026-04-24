FROM node:22-alpine
WORKDIR /app
RUN npm install -g @mariozechner/pi-coding-agent
COPY package.json ./
RUN npm install
COPY src/ ./src/

# Create non-root user
RUN addgroup -S piagent && adduser -S piagent -G piagent \
    && mkdir -p /home/piagent/.pi/agent && chown -R piagent:piagent /home/piagent/.pi

# Entrypoint fixes /library ownership then drops to piagent
RUN apk add --no-cache su-exec
COPY entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh

ENTRYPOINT ["/entrypoint.sh"]
CMD ["npx", "tsx", "src/index.ts"]
