#!/usr/bin/env bash

# Pure readiness predicate shared by the Docker provider gate and its regression
# test. Guard-marker visibility is not enough during first-time image init: the
# official entrypoints briefly run temporary database servers before execing the
# final PID 1 daemons.
sql_gate_final_daemons_ready() {
  local pg_pid1="$1" my_pid1="$2" pg_marker="$3" my_marker="$4" expected_marker="$5"
  [ "$pg_pid1" = postgres ] && \
    [ "$my_pid1" = mysqld ] && \
    [ "$pg_marker" = "$expected_marker" ] && \
    [ "$my_marker" = "$expected_marker" ]
}
