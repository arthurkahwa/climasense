"""Tests for `ChangepointDetector` SQL shape pinning + transaction shape.

Three concerns:

  * The SQL statements target `dbo.Anomalies` and use
    `sp_getapplock` with the well-known resource name
    `changepoint_scan`.
  * The DELETE clears only `regime_shift` rows in the scan window.
  * Hyperparameters (`pen=10`, `DEFAULT_DAYS=90`) are stable.

The golden test for idempotency is in
`test_changepoint_scan_and_replace_idempotent.py`.
"""

from __future__ import annotations

from climasense_ml import anomaly_changepoint
from climasense_ml.anomaly_changepoint import (
    APPLOCK_RESOURCE,
    DEFAULT_DAYS,
    DEFAULT_PELT_PENALTY,
    MIN_DAILY_POINTS,
)


def test_applock_acquire_sql_uses_named_resource_and_exclusive_mode() -> None:
    sql = str(anomaly_changepoint._APPLOCK_ACQUIRE_SQL).upper()
    assert "SP_GETAPPLOCK" in sql
    assert "EXCLUSIVE" in sql
    assert "TRANSACTION" in sql
    assert ":RESOURCE" in sql
    # Pinned in the module so the .NET tier (future) can refer to the
    # same lock name without grep diving.
    assert APPLOCK_RESOURCE == "changepoint_scan"


def test_delete_sql_scopes_to_regime_shift_in_window() -> None:
    sql = str(anomaly_changepoint._DELETE_SQL).upper()
    assert "DELETE FROM DBO.ANOMALIES" in sql
    assert "ANOMALYTYPE = 'REGIME_SHIFT'" in sql
    assert "READINGTIME >= :WINDOW_START" in sql
    assert "READINGTIME <= :WINDOW_END" in sql


def test_insert_sql_targets_anomalies_with_regime_shift_type() -> None:
    sql = str(anomaly_changepoint._INSERT_SQL).upper()
    assert "INSERT INTO DBO.ANOMALIES" in sql
    assert "'REGIME_SHIFT'" in sql
    # The changepoint detector does NOT use WHERE NOT EXISTS — the
    # scan-and-replace transaction handles idempotency by deleting
    # then re-inserting.
    assert "WHERE NOT EXISTS" not in sql


def test_module_constants_remain_pinned() -> None:
    assert DEFAULT_DAYS == 90
    assert DEFAULT_PELT_PENALTY == 10.0
    assert MIN_DAILY_POINTS == 14
