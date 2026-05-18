"""Tests for `anomaly_persistence` SQL shape pinning.

The read SQL targets `dbo.fv_anomalies_at_cursor` (cursor-clipping is
a property of the schema) and carries the cursor parameter. Behaviour
is exercised end-to-end through the router test; the shape test here
guards against accidental drift in the column projection or the
cursor seam.
"""

from __future__ import annotations

from climasense_ml import anomaly_persistence


def test_recent_sql_reads_through_cursor_tvf_with_since_bound() -> None:
    sql = str(anomaly_persistence._RECENT_SQL).upper()
    assert "FV_ANOMALIES_AT_CURSOR" in sql
    assert ":AS_OF" in sql
    assert ":SINCE" in sql
    assert "READINGTIME >= :SINCE" in sql
    assert "ORDER BY READINGTIME DESC" in sql
    # Column projection — guards against the schema growing a column
    # that the dashboard JS doesn't know how to render.
    for col in ("ANOMALYID", "READINGTIME", "ANOMALYTYPE", "SEVERITY",
                "SCORE", "DESCRIPTION", "DETECTEDAT"):
        assert col in sql


def test_counts_sql_reads_through_cursor_tvf_and_groups_by_type() -> None:
    sql = str(anomaly_persistence._COUNTS_SQL).upper()
    assert "FV_ANOMALIES_AT_CURSOR" in sql
    assert "GROUP BY ANOMALYTYPE" in sql
    assert "COUNT(*)" in sql
