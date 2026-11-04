FROM mcr.microsoft.com/playwright:v1.64.0-noble
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci --omit=dev
COPY server.mjs ./
ENV HOST=0.0.0.0 PORT=5299
USER pwuser
EXPOSE 5299
CMD ["node", "server.mjs"]
