#!/usr/bin/env bash
# =============================================================================
#  DealFlow end-to-end smoke test
#
#  Proves the whole pipeline works:
#    API  ->  Service Bus  ->  Worker (Azure Functions)  ->  Azure OpenAI  ->  Cosmos DB  ->  API
#
#  Run from the repo root:   ./scripts/smoke-test.sh
#  Needs: az (logged in), terraform (initialized in infra/), curl, jq   (brew install jq)
#  Works with the bash that ships with macOS (3.2).
# =============================================================================
set -uo pipefail
cd "$(dirname "$0")/.."

# settings -------------------------------------------------------------------
API_URL="${API_URL:-$(terraform -chdir=infra output -raw api_url)}"
RG="${RG:-$(terraform -chdir=infra output -raw resource_group)}"
FUNC="${FUNC:-$(terraform -chdir=infra output -raw function_app_name)}"
SB="sb-${RG#rg-}"     # rg-dealflow-dev-xxxxx  ->  sb-dealflow-dev-xxxxx
TIMEOUT=120           # max seconds to wait per run (Flex cold start can take ~1 min)

# styling --------------------------------------------------------------------
if [ -t 1 ]; then
  BOLD=$'\033[1m'; DIM=$'\033[2m'; RED=$'\033[31m'; GREEN=$'\033[32m'
  YELLOW=$'\033[33m'; CYAN=$'\033[36m'; RESET=$'\033[0m'; CLEAR=$'\033[K'
else
  BOLD=""; DIM=""; RED=""; GREEN=""; YELLOW=""; CYAN=""; RESET=""; CLEAR=""
fi

PASS=0; FAIL=0; WARN=0
pass() { printf "   ${GREEN}✅  %s${RESET}\n" "$1"; PASS=$((PASS+1)); }
fail() { printf "   ${RED}❌  %s${RESET}\n" "$1"; FAIL=$((FAIL+1)); }
warn() { printf "   ${YELLOW}⚠️   %s${RESET}\n" "$1"; WARN=$((WARN+1)); }
info() { printf "   ${DIM}%s${RESET}\n" "$1"; }
step() { printf "\n${BOLD}${CYAN}[%s/6] %s${RESET}\n" "$1" "$2"; }

# header ---------------------------------------------------------------------
printf "\n${BOLD}╔══════════════════════════════════════════════════════╗${RESET}\n"
printf   "${BOLD}║            DealFlow  ·  End-to-end smoke test        ║${RESET}\n"
printf   "${BOLD}╚══════════════════════════════════════════════════════╝${RESET}\n"
info "API:       $API_URL"
info "Worker:    $FUNC"
info "Queue:     $SB/deals"

# 1. API is reachable =========================================================
step 1 "API health check"
code=$(curl -s -o /dev/null -w "%{http_code}" "$API_URL/health")
if [ "$code" = "200" ]; then pass "API is up (GET /health → 200)"
else fail "API not healthy (GET /health → $code)"; fi

# 2. Bad input gets rejected =================================================
step 2 "Input validation"
code=$(curl -s -o /dev/null -w "%{http_code}" -X POST "$API_URL/deals" \
  -H "Content-Type: application/json" -d '{"accountName":"","region":"MARS","amountUsd":-5}')
if [ "$code" = "400" ]; then pass "Invalid deal rejected (→ 400)"
else fail "Invalid deal returned $code (expected 400)"; fi

# 3. Worker is registered ====================================================
step 3 "Worker registration"
if az functionapp function list -g "$RG" -n "$FUNC" --query "[].name" -o tsv 2>/dev/null | grep -q "ProcessDeal"; then
  pass "ProcessDeal function is registered"
else
  fail "ProcessDeal not registered — worker is not running"
  info "Wake it with: az functionapp keys list -g $RG -n $FUNC"
fi

# 4. Submit test deals =======================================================
step 4 "Submit test deals"
IDS=""; NAMES=""
submit() {  # $1 account  $2 region  $3 amount  $4 notes
  local body resp code id
  body=$(jq -nc --arg a "$1" --arg r "$2" --argjson m "$3" --arg n "$4" \
    '{accountName:$a, region:$r, amountUsd:$m, notes:$n}')
  resp=$(curl -s -w $'\n%{http_code}' -X POST "$API_URL/deals" -H "Content-Type: application/json" -d "$body")
  code=$(printf "%s" "$resp" | tail -n1)
  id=$(printf "%s" "$resp" | head -n1 | jq -r '.id // empty' 2>/dev/null)
  if [ "$code" = "202" ] && [ -n "$id" ]; then
    pass "$(printf '%-10s %-6s $%-8s → 202 Accepted' "$1" "$2" "$3")"
    IDS="$IDS $id"; NAMES="$NAMES $1"
  else
    fail "$1 → $code (expected 202)"
  fi
}
submit "Northwind" "NA"    450000 "Multi-year renewal, competitor eval, exec sponsor wants signature this week"
submit "Contoso"   "LATAM"  90000 "Expansion into two new regions next quarter"
submit "Fabrikam"  "APAC"    8000 "Small pilot, no deadline"

# 5. Wait for the worker =====================================================
step 5 "Worker processing  (API → Service Bus → Worker → OpenAI → Cosmos)"
start=$(date +%s)
ROWS=""
for id in $IDS; do
  while true; do
    elapsed=$(( $(date +%s) - start ))
    resp=$(curl -s -w $'\n%{http_code}' "$API_URL/deals/$id")
    code=$(printf "%s" "$resp" | tail -n1)
    json=$(printf "%s" "$resp" | head -n1)

    if [ "$code" = "200" ] && [ "$(printf "%s" "$json" | jq -r .status)" = "Processed" ]; then
      printf "\r${CLEAR}"
      name=$(printf "%s" "$json" | jq -r .accountName)
      prio=$(printf "%s" "$json" | jq -r .priority)
      by=$(printf "%s" "$json"   | jq -r .enrichedBy)
      summ=$(printf "%s" "$json" | jq -r .summary)
      pass "$(printf '%-10s processed in ~%ss' "$name" "$elapsed")"
      [ "$by" = "ai" ] || warn "$name used the rules fallback (AI flag off or OpenAI call failed)"
      ROWS="$ROWS$(printf '%-10s|%-6s|%-5s|%s' "$name" "$prio" "$by" "$summ")"$'\n'
      break
    fi

    if [ "$elapsed" -ge "$TIMEOUT" ]; then
      printf "\r${CLEAR}"
      fail "Deal $id not processed after ${TIMEOUT}s (last HTTP $code)"
      break
    fi
    printf "\r   ${YELLOW}⏳  waiting for worker… %3ss${RESET}${CLEAR}" "$elapsed"
    sleep 3
  done
done

# 6. Queue fully drained =====================================================
step 6 "Queue health"
counts=$(az servicebus queue show -g "$RG" --namespace-name "$SB" -n deals \
  --query "{a:countDetails.activeMessageCount, d:countDetails.deadLetterMessageCount}" -o json 2>/dev/null)
active=$(printf "%s" "$counts" | jq -r '.a // "?"'); dead=$(printf "%s" "$counts" | jq -r '.d // "?"')
if [ "$active" = "0" ]; then pass "No messages waiting (active = 0)"
else fail "Messages still waiting (active = $active) — worker not consuming"; fi
if [ "$dead" = "0" ]; then pass "No failed messages (dead-letter = 0)"
else fail "Failed messages (dead-letter = $dead) — peek the DLQ in Service Bus Explorer"; fi

# results ====================================================================
if [ -n "$ROWS" ]; then
  printf "\n${BOLD}Processed deals${RESET}\n"
  printf "   ${DIM}%-10s  %-6s  %-5s  %s${RESET}\n" "ACCOUNT" "PRIO" "BY" "SUMMARY"
  printf "%s" "$ROWS" | while IFS='|' read -r n p b s; do
    [ -n "$n" ] && printf "   %-10s  %-6s  %-5s  %s\n" "$n" "$p" "$b" "$s"
  done
fi

printf "\n${BOLD}══════════════════════════════════════════════════════${RESET}\n"
if [ "$FAIL" -eq 0 ]; then
  printf "${BOLD}${GREEN}  ALL CHECKS PASSED${RESET}   %s passed · %s warnings\n" "$PASS" "$WARN"
else
  printf "${BOLD}${RED}  %s CHECK(S) FAILED${RESET}   %s passed · %s warnings\n" "$FAIL" "$PASS" "$WARN"
fi
printf "${BOLD}══════════════════════════════════════════════════════${RESET}\n\n"
[ "$FAIL" -eq 0 ]