#!/bin/sh
set -eu
[ "${SQL_TEST_ENGINE_GATE:-}" = 1 ] || { echo 'Refusing an ungated engine integration run.' >&2; exit 2; }
! command -v python >/dev/null 2>&1
! command -v python3 >/dev/null 2>&1
/app/sql-worker version
/app/sql-worker self-test-isolation
/checks/sqlworker.test -test.v -test.timeout=10m
/checks/wakeup.test -test.v -test.timeout=1m
