FROM node:22-alpine
WORKDIR /app
RUN npm install -g @mariozechner/pi-coding-agent
COPY src/ ./src/
COPY package.json ./
RUN npm install
EXPOSE 3001
CMD ["node", "src/index.ts"]
