FROM node:22-alpine AS build
WORKDIR /app
COPY package.json package-lock.json* ./
RUN npm ci
COPY . .
ARG VITE_API_BASE_URL=/api
ARG VITE_AUTH_CLIENT_ID=ai-spend-web
ARG VITE_AUTH_ISSUER=https://auth.mercantec.tech
ARG VITE_AUTH_REDIRECT_URI=
ENV VITE_API_BASE_URL=$VITE_API_BASE_URL \
    VITE_AUTH_CLIENT_ID=$VITE_AUTH_CLIENT_ID \
    VITE_AUTH_ISSUER=$VITE_AUTH_ISSUER \
    VITE_AUTH_REDIRECT_URI=$VITE_AUTH_REDIRECT_URI
RUN npm run build

FROM nginxinc/nginx-unprivileged:alpine AS final
USER root
RUN apk add --no-cache gettext
COPY nginx.conf.template /etc/nginx/templates/default.conf.template
COPY docker-entrypoint.sh /aispend-entrypoint.sh
RUN chmod +x /aispend-entrypoint.sh
USER 101
COPY --from=build /app/dist /usr/share/nginx/html
ENV AISPEND_API_URL=http://aispend-api:8080
ENTRYPOINT ["/aispend-entrypoint.sh"]
CMD ["nginx", "-g", "daemon off;"]
EXPOSE 3000
