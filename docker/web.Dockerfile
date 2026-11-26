# One Dockerfile for both React apps; compose picks the app folder with the APP build arg.
# Vite bakes VITE_* values into the JavaScript at build time. The browser runs on the host, not in
# Docker, so these stay localhost URLs (the host ports compose publishes), not container names.
FROM node:22-alpine AS build
ARG APP
WORKDIR /app
COPY ${APP}/package.json ${APP}/package-lock.json ./
RUN npm ci
COPY ${APP}/ .
ARG VITE_API_URL=http://localhost:5081
ARG VITE_DIRECTORY_URL=http://localhost:5090
ENV VITE_API_URL=${VITE_API_URL} VITE_DIRECTORY_URL=${VITE_DIRECTORY_URL}
RUN npm run build

FROM nginx:1.27-alpine
COPY docker/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist /usr/share/nginx/html
EXPOSE 80
