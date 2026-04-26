FROM node:22-alpine
WORKDIR /app
RUN npm install -g @mariozechner/pi-coding-agent
COPY package.json ./
RUN npm install
COPY src/ ./src/

# Create non-root user with uid 100 (same as api container)
RUN addgroup -g 100 -S piagent && adduser -u 100 -S piagent -G piagent \
    && mkdir -p /home/piagent/.pi/agent && chown -R piagent:piagent /home/piagent/.pi

USER piagent
EXPOSE 3001
CMD ["npx", "tsx", "src/index.ts"]
