"""Tests for `comfort_persistence` SQL shape pinning.

These tests are intentionally shape-only — they assert the SQL targets
the right tables/TVFs and carries the cursor parameter, without
running it against SQL Server (the test fixture has no DB available).

The MERGE upsert is exercised end-to-end through
`test_comfort_emitter.py` and `test_comfort_router.py`; the SQL
shape lock here guards against accidental drift in column names or
the cursor-clipping discipline.
"""

from __future__ import annotations

from climasense_ml import comfort_persistence


def test_merge_sql_targets_comfortscores_and_keys_on_bucket() -> None:
    sql = str(comfort_persistence._MERGE_SQL).upper()
    assert "MERGE DBO.COMFORTSCORES" in sql
    assert "TARGET.BUCKETTIME = SRC.BUCKETTIME" in sql
    assert "WHEN MATCHED" in sql
    assert "WHEN NOT MATCHED" in sql
    # Make sure we update all four mutable columns on match.
    assert "SCORE = SRC.SCORE" in sql
    assert "RATING = SRC.RATING" in sql
    assert "SEASON = SRC.SEASON" in sql


def test_latest_sql_reads_through_cursor_tvf() -> None:
    sql = str(comfort_persistence._LATEST_SQL).upper()
    assert "FV_COMFORTSCORES_AT_CURSOR" in sql
    assert ":AS_OF" in sql
    assert "TOP 1" in sql
    assert "ORDER BY BUCKETTIME DESC" in sql


def test_recent_sql_reads_through_cursor_tvf_with_window() -> None:
    sql = str(comfort_persistence._RECENT_SQL).upper()
    assert "FV_COMFORTSCORES_AT_CURSOR" in sql
    assert ":AS_OF" in sql
    assert ":START" in sql
    assert "ORDER BY BUCKETTIME ASC" in sql
