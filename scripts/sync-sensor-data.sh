#!/usr/bin/env bash
#
# sync-sensor-data.sh  —  one-off data sync for ups3.dbo.tbl_sensor_data
# ----------------------------------------------------------------------
#   apsrv007vsn  (only reachable over the FaChapter VPN)  ->  tbl_sensor_data.csv
#   tbl_sensor_data.csv  ->  util02.lab.local             (local LAN; the DB the
#                                                          ClimaSense Monitor reads)
#
#   (1) connect VPN   (2/3) dump source table -> CSV   (4) disconnect VPN
#   (5/6) load CSV into util02, replacing sensor_dateTime / temperature / humidity.
#
# !!! DESTRUCTIVE on util02 !!!  Step 6 replaces the live table the app reads from.
# Safeguards: a timestamped backup table is made first; the load goes through a
# staging table and a transactional TRUNCATE+INSERT; an explicit confirmation is
# required (unless ASSUME_YES=1).
#
# This file is intentionally git-ignored (infra hostnames + a run-once ops tool).
#
# Requirements (macOS):
#   * Tunnelblick, with this VPN config already imported AND its credentials saved
#     (Benutzerauthentifizierung) so it connects without an interactive GUI prompt.
#       VPN config: OpenVPN_via_SSL_TLS___Benutzerauthentifizierung_FaChapter_kaar.tblk
#   * mssql-tools18 on PATH (provides sqlcmd + bcp):
#       brew tap microsoft/mssql-release https://github.com/microsoft/homebrew-mssql-release
#       brew update && ACCEPT_EULA=Y brew install msodbcsql18 mssql-tools18
#       export PATH="$PATH:/opt/homebrew/opt/mssql-tools18/bin"   # Apple Silicon
#
# Credentials — export before running (never hard-code or commit):
#       export SRC_USER=...  SRC_PASSWORD=...      # apsrv007vsn login
#       export DST_USER=...  DST_PASSWORD=...      # util02.lab.local login
#
# Usage:
#       ./scripts/sync-sensor-data.sh              # asks for confirmation before the replace
#       ASSUME_YES=1 ./scripts/sync-sensor-data.sh # unattended (skips the prompt)
#
# ASSUMPTIONS to verify for your schema (adjust the CONFIG block if they differ):
#   * Both servers hold ups3.dbo.tbl_sensor_data with columns
#     sensor_dateTime (datetime, sub-second), temperature (int), humidity (int),
#     plus an IDENTITY id that is auto-generated on insert.
#   * util02's table has no other NOT NULL columns without defaults (else the
#     INSERT step fails — and rolls back, leaving the table unchanged).

set -euo pipefail

# ----------------------------------------------------------------- config --
VPN_CONFIG_NAME="${VPN_CONFIG_NAME:-OpenVPN_via_SSL_TLS___Benutzerauthentifizierung_FaChapter_kaar}"
SRC_SERVER="${SRC_SERVER:-apsrv007vsn}"
DST_SERVER="${DST_SERVER:-util02.lab.local}"
DB="${DB:-ups3}"
TBL="${TBL:-dbo.tbl_sensor_data}"
CSV="${CSV:-tbl_sensor_data.csv}"
VPN_TIMEOUT="${VPN_TIMEOUT:-120}"        # seconds to wait for a VPN state change

: "${SRC_USER:?export SRC_USER (apsrv007vsn login)}"
: "${SRC_PASSWORD:?export SRC_PASSWORD}"
: "${DST_USER:?export DST_USER (util02 login)}"
: "${DST_PASSWORD:?export DST_PASSWORD}"

STAMP="$(date +%Y%m%d_%H%M%S)"
BACKUP_TBL="dbo.tbl_sensor_data_backup_${STAMP}"     # date-stamped copy kept for restore
STAGING_TBL="dbo.tbl_sensor_data_staging_${STAMP}"

log()  { printf '\n\033[1;36m== %s\033[0m\n' "$*"; }
warn() { printf '\033[1;33mWARN: %s\033[0m\n' "$*" >&2; }
die()  { printf '\033[1;31mERROR: %s\033[0m\n' "$*" >&2; exit 1; }
is_num() { [[ "${1:-}" =~ ^[0-9]+$ ]]; }

# --------------------------------------------------------------- preflight --
command -v osascript >/dev/null || die "osascript not found (this script is macOS/Tunnelblick only)."
command -v bcp       >/dev/null || die "bcp not found — install mssql-tools18 (see header)."
command -v sqlcmd    >/dev/null || die "sqlcmd not found — install mssql-tools18 (see header)."

# sqlcmd18 / bcp18 encrypt by default; trust the (self-signed) server cert with
# sqlcmd's -C below, and bcp's -u (note: in bcp, -C means code page, not trust).
src_sql() { sqlcmd -S "$SRC_SERVER" -d "$DB" -U "$SRC_USER" -P "$SRC_PASSWORD" -C -b -h-1 -W "$@"; }
dst_sql() { sqlcmd -S "$DST_SERVER" -d "$DB" -U "$DST_USER" -P "$DST_PASSWORD" -C -b -h-1 -W "$@"; }
scalar()  { "$1" -Q "SET NOCOUNT ON; $2" 2>/dev/null | head -1 | tr -d '[:space:]'; }   # $1 = src_sql|dst_sql

# --------------------------------------------------------------------- VPN --
vpn_state() { osascript -e "tell application \"Tunnelblick\" to get state of first configuration where name = \"$VPN_CONFIG_NAME\"" 2>/dev/null || echo UNKNOWN; }
vpn_wait()  { # $1 = desired predicate: "CONNECTED" (equals) or "!CONNECTED" (not equals)
  local want="$1" i=0
  while :; do
    local s; s="$(vpn_state)"
    if [ "$want" = "CONNECTED"  ] && [ "$s" = "CONNECTED" ]; then return 0; fi
    if [ "$want" = "!CONNECTED" ] && [ "$s" != "CONNECTED" ]; then return 0; fi
    sleep 2; i=$((i+2)); [ "$i" -ge "$VPN_TIMEOUT" ] && return 1
  done
}
vpn_connect() {
  log "Step 1 — connecting VPN ($VPN_CONFIG_NAME)"
  osascript -e "tell application \"Tunnelblick\" to connect \"$VPN_CONFIG_NAME\"" >/dev/null
  vpn_wait CONNECTED || die "VPN did not reach CONNECTED within ${VPN_TIMEOUT}s (state: $(vpn_state)). Saved credentials?"
  log "VPN connected."
}
vpn_disconnect() {
  log "Step 4 — disconnecting VPN"
  osascript -e "tell application \"Tunnelblick\" to disconnect \"$VPN_CONFIG_NAME\"" >/dev/null || true
  vpn_wait '!CONNECTED' || warn "VPN still not disconnected after ${VPN_TIMEOUT}s (state: $(vpn_state))."
  log "VPN state: $(vpn_state)."
}

# Always drop the VPN if we are the ones who brought it up, however we exit.
VPN_UP=0
trap '[ "$VPN_UP" = 1 ] && [ "$(vpn_state)" = CONNECTED ] && vpn_disconnect || true' EXIT

# ------------------------------------------------------------------- run! --
vpn_connect; VPN_UP=1

log "Step 2/3 — dump ${SRC_SERVER}.${DB}.${TBL} -> ${CSV}"
src_count="$(scalar src_sql "SELECT COUNT(*) FROM ${TBL};")"
is_num "$src_count" && [ "$src_count" -ge 1 ] || die "Source table empty/unreadable (count='${src_count}') — refusing to continue."
log "Source rows: $src_count"
bcp "SELECT sensor_dateTime, temperature, humidity FROM ${DB}.${TBL} ORDER BY sensor_dateTime" queryout "$CSV" \
    -S "$SRC_SERVER" -U "$SRC_USER" -P "$SRC_PASSWORD" -d "$DB" -c -t',' -u \
  || die "bcp dump failed."
csv_rows="$(wc -l < "$CSV" | tr -d '[:space:]')"
is_num "$csv_rows" && [ "$csv_rows" -ge 1 ] || die "CSV '$CSV' is empty — refusing to continue (nothing written to ${DST_SERVER})."
log "Wrote $CSV ($csv_rows rows)."

vpn_disconnect; VPN_UP=0

log "Step 5 — target ${DST_SERVER}.${DB}.${TBL}"
dst_count="$(scalar dst_sql "SELECT COUNT(*) FROM ${TBL};")"
is_num "$dst_count" || die "Cannot reach ${DST_SERVER} / read target table (got '${dst_count}')."
log "Target currently has $dst_count rows; CSV has $csv_rows rows."

# --- confirmation gate (this is the destructive point) ---
if [ "${ASSUME_YES:-0}" != "1" ]; then
  printf '\n\033[1;33mAbout to REPLACE all data in %s on %s with %s (%s rows).\nA backup table %s is created first.\nType the target server name (%s) to proceed: \033[0m' \
    "$TBL" "$DST_SERVER" "$CSV" "$csv_rows" "$BACKUP_TBL" "$DST_SERVER"
  read -r reply
  [ "$reply" = "$DST_SERVER" ] || die "Confirmation did not match — aborted. ${DST_SERVER} is unchanged."
fi

log "Step 6 — backup -> stage -> transactional swap"
dst_sql -Q "SET NOCOUNT ON; SELECT * INTO ${BACKUP_TBL} FROM ${TBL};" \
  || die "Backup failed — aborted before any change to ${TBL}."
log "Backup created: ${BACKUP_TBL}"

dst_sql -Q "SET NOCOUNT ON;
IF OBJECT_ID('${STAGING_TBL}') IS NOT NULL DROP TABLE ${STAGING_TBL};
CREATE TABLE ${STAGING_TBL} (sensor_dateTime datetime2(3) NOT NULL, temperature int NOT NULL, humidity int NOT NULL);" \
  || die "Could not create staging table (backup ${BACKUP_TBL} is intact)."

bcp "${STAGING_TBL}" in "$CSV" -S "$DST_SERVER" -U "$DST_USER" -P "$DST_PASSWORD" -d "$DB" -c -t',' -u \
  || die "Bulk load into staging failed (target ${TBL} untouched; backup ${BACKUP_TBL} kept)."

stg_count="$(scalar dst_sql "SELECT COUNT(*) FROM ${STAGING_TBL};")"
is_num "$stg_count" || die "Could not count staging rows."
[ "$stg_count" = "$src_count" ] || warn "staged ($stg_count) != source ($src_count) — review before trusting the result."
log "Staged $stg_count rows."

dst_sql -Q "SET NOCOUNT ON; SET XACT_ABORT ON;
BEGIN TRAN;
  TRUNCATE TABLE ${TBL};
  INSERT INTO ${TBL} (sensor_dateTime, temperature, humidity)
    SELECT sensor_dateTime, temperature, humidity FROM ${STAGING_TBL};
COMMIT;
DROP TABLE ${STAGING_TBL};" \
  || die "Swap transaction failed and ROLLED BACK — ${TBL} unchanged; backup ${BACKUP_TBL} kept; staging ${STAGING_TBL} may remain."

final_count="$(scalar dst_sql "SELECT COUNT(*) FROM ${TBL};")"
log "DONE — ${TBL} now has ${final_count} rows (was ${dst_count})."
log "Backup retained: ${BACKUP_TBL}.  When you're satisfied:  DROP TABLE ${BACKUP_TBL};"
