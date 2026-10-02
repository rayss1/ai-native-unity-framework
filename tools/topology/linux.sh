#!/bin/sh
set -eu
cd "$(dirname "$0")/../.."
case "${1:-}" in
 init) python3 tools/topology/initialize-compose.py ;;
 build) docker compose -f infrastructure/topology/compose.yaml build ;;
 start) docker compose -f infrastructure/topology/compose.yaml up -d ;;
 stop) docker compose -f infrastructure/topology/compose.yaml stop ;;
 logs) docker compose -f infrastructure/topology/compose.yaml logs --no-color ;;
 *) echo 'usage: linux.sh init|build|start|stop|logs' >&2; exit 2 ;;
esac
