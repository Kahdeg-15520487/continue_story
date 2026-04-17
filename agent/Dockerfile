FROM node:22-alpine
WORKDIR /app
RUN npm install -g @mariozechner/pi-coding-agent
COPY package.json ./
RUN npm install
COPY src/ ./src/
EXPOSE 3001
CMD ["npx", "tsx", "src/index.ts"]
