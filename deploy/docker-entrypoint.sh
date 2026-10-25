#!/bin/sh
# Starts the app. With LITESTREAM_BUCKET set, the SQLite database is first restored from the
# backup when the disk is empty (new server, lost disk), then copied there continuously.
set -e

export LITESTREAM_DB_PATH="${DataDir:-/app/data}/invoices.db"

if [ -n "$LITESTREAM_BUCKET" ]; then
  litestream restore -if-db-not-exists -if-replica-exists "$LITESTREAM_DB_PATH"
  exec litestream replicate -exec "dotnet InvoiceReader.Web.dll"
fi

exec dotnet InvoiceReader.Web.dll
