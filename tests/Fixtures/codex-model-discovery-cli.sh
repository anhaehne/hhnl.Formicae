#!/usr/bin/env bash
set -euo pipefail
[[ "$*" == '-y @openai/codex app-server --listen stdio://' ]]
[[ -f "$CODEX_HOME/auth.json" ]]
read -r initialize
[[ "$initialize" == *'"method":"initialize"'* ]]
printf '%s\n' '{"id":1,"result":{}}'
read -r initialized
[[ "$initialized" == '{"method":"initialized"}' ]]
read -r list
[[ "$list" == *'"method":"model/list"'* ]]
printf '%s\n' '{"id":2,"result":{"data":[{"id":"fixture","model":"fixture-model","displayName":"Fixture model","isDefault":true}],"nextCursor":null}}'
read -r forever
