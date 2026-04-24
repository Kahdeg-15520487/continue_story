FROM node:22-alpine
WORKDIR /app
RUN npm install -g @mariozechner/pi-coding-agent
COPY package.json ./
RUN npm install
COPY src/ ./src/

# Run as non-root user
RUN addgroup -S piagent && adduser -S piagent -G piagent
RUN mkdir -p /home/piagent/.pi/agent && chown -R piagent:piagent /home/piagent/.pi
USER piagent

EXPOSE 3001
CMD ["npx", "tsx", "src/index.ts"]
