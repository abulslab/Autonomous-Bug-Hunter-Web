# Build from the repository root: docker build -f src/web/Dockerfile --build-arg NEXT_PUBLIC_API_URL=http://localhost:5280 .
FROM node:20-alpine AS build
WORKDIR /web
COPY src/web/package.json src/web/package-lock.json ./
RUN npm ci
COPY src/web/ ./
# The browser calls the API directly, so its public URL is baked into the client bundle at build time.
ARG NEXT_PUBLIC_API_URL=http://localhost:5280
ENV NEXT_PUBLIC_API_URL=$NEXT_PUBLIC_API_URL NEXT_TELEMETRY_DISABLED=1
RUN npm run build

FROM node:20-alpine
WORKDIR /web
ENV NODE_ENV=production PORT=3100 HOSTNAME=0.0.0.0 NEXT_TELEMETRY_DISABLED=1
COPY --from=build /web/.next/standalone ./
COPY --from=build /web/.next/static ./.next/static
COPY --from=build /web/public ./public
EXPOSE 3100
USER node
CMD ["node", "server.js"]
