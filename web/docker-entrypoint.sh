#!/bin/sh
set -eu
: "${AISPEND_API_URL:=http://aispend-api:8080}"
export AISPEND_API_URL
envsubst '${AISPEND_API_URL}' < /etc/nginx/templates/default.conf.template > /etc/nginx/conf.d/default.conf
exec "$@"
