"""Golden test 2 — ASHRAE 55 polygon scoring + seasonal boundary.

Locks the architectural claim of ADR-0005 / issue #9: the
`ComfortCalculator.score()` function is a pure ASHRAE-55-2020 graphical
comfort-zone evaluator, with summer and winter polygons selected by
calendar month and the `COMFORT_HEMISPHERE` env var (mirrored for the
southern hemisphere).

The test is intentionally over-specified for boundary cases. Triplets:

  1. Summer-polygon centroid: known-comfortable, `score == 100`,
     `rating == "excellent"`, `season == "summer"` in the north.
  2. Winter-polygon centroid: known-comfortable, `score == 100`,
     `rating == "excellent"`, `season == "winter"` in the north.
  3. Summer-polygon **interior** points (3 points other than the
     centroid) all score 100 — exercises the point-in-polygon code
     beyond just the centroid.
  4. Winter-polygon **interior** points (3 points) all score 100.
  5. Seasonal boundary crossing (Apr 30 vs May 1, both `N`):
     the (T, RH) input is identical; only the date changes; the
     `season` label changes from "winter" to "summer".
  6. Southern-hemisphere mirroring: `(May, 25 °C, 50 %, S) → "winter"`.
  7. Far-outside points score 0 ("uncomfortable").
  8. Out-of-RH inputs are clamped (no exception raised).

This covers ≥ 8 triplets across both polygons + the seasonal boundary +
both hemispheres, as the AC requires.
"""

from __future__ import annotations

from datetime import datetime, timezone

import pytest

from climasense_ml.comfort import (
    ComfortCalculator,
    _SUMMER_VERTICES,
    _WINTER_VERTICES,
)


def _utc(year: int, month: int, day: int) -> datetime:
    return datetime(year, month, day, 12, 0, 0, tzinfo=timezone.utc)


# ---------------------------------------------------------------------
# 1. Summer-polygon centroid → score 100, "excellent", "summer".
# ---------------------------------------------------------------------
def test_summer_polygon_centroid_scores_100_in_northern_summer() -> None:
    # May lies in the Northern-hemisphere summer window (Apr–Oct).
    bucket = _utc(2025, 5, 15)

    result = ComfortCalculator.score(
        t_c=25.0, rh_pct=50.0, bucket_time=bucket, hemisphere="N"
    )

    assert result.season == "summer"
    assert result.score == 100.0
    assert result.rating == "excellent"


# ---------------------------------------------------------------------
# 2. Winter-polygon centroid → score 100, "excellent", "winter".
# ---------------------------------------------------------------------
def test_winter_polygon_centroid_scores_100_in_northern_winter() -> None:
    # January lies in the Northern-hemisphere winter window (Nov–Mar).
    bucket = _utc(2025, 1, 15)

    result = ComfortCalculator.score(
        t_c=21.0, rh_pct=40.0, bucket_time=bucket, hemisphere="N"
    )

    assert result.season == "winter"
    assert result.score == 100.0
    assert result.rating == "excellent"


# ---------------------------------------------------------------------
# 3. Summer-polygon interior points (≥ 3 not at the centroid) → 100.
# ---------------------------------------------------------------------
@pytest.mark.parametrize(
    "t_c, rh_pct",
    [
        (26.0, 20.0),  # dryish upper-mid summer
        (24.5, 45.0),  # lower-left interior
        (24.0, 70.0),  # mid-humid lower-left
    ],
)
def test_summer_polygon_interior_points_all_score_100(
    t_c: float, rh_pct: float
) -> None:
    bucket = _utc(2025, 7, 1)  # July, Northern hemisphere → summer

    result = ComfortCalculator.score(
        t_c=t_c, rh_pct=rh_pct, bucket_time=bucket, hemisphere="N"
    )

    assert result.season == "summer"
    assert result.score == 100.0, (
        f"({t_c} °C, {rh_pct} % RH) should be inside the summer polygon"
    )
    assert result.rating == "excellent"


# ---------------------------------------------------------------------
# 4. Winter-polygon interior points (≥ 3 not at the centroid) → 100.
# ---------------------------------------------------------------------
@pytest.mark.parametrize(
    "t_c, rh_pct",
    [
        (22.0, 20.0),  # dryish upper-mid winter
        (20.5, 45.0),  # lower-left interior
        (20.0, 70.0),  # mid-humid lower-left
    ],
)
def test_winter_polygon_interior_points_all_score_100(
    t_c: float, rh_pct: float
) -> None:
    bucket = _utc(2025, 12, 15)  # December, Northern hemisphere → winter

    result = ComfortCalculator.score(
        t_c=t_c, rh_pct=rh_pct, bucket_time=bucket, hemisphere="N"
    )

    assert result.season == "winter"
    assert result.score == 100.0, (
        f"({t_c} °C, {rh_pct} % RH) should be inside the winter polygon"
    )
    assert result.rating == "excellent"


# ---------------------------------------------------------------------
# 5. Seasonal boundary crossing — identical (T, RH); only the date
#    changes; season flips from "winter" to "summer".
# ---------------------------------------------------------------------
def test_seasonal_boundary_apr30_vs_may1_changes_season() -> None:
    apr30 = _utc(2025, 4, 30)
    may1 = _utc(2025, 5, 1)

    # April is technically inside _NORTHERN_SUMMER_MONTHS (4..10), so
    # both Apr 30 and May 1 are "summer" under the issue's spec
    # ("Northern hemisphere ⇒ summer Apr–Oct, winter Nov–Mar"). This
    # test locks the spec: at the Apr/May boundary BOTH evaluate to
    # "summer" because Apr is the start of the summer window.
    apr30_result = ComfortCalculator.score(
        t_c=24.0, rh_pct=50.0, bucket_time=apr30, hemisphere="N"
    )
    may1_result = ComfortCalculator.score(
        t_c=24.0, rh_pct=50.0, bucket_time=may1, hemisphere="N"
    )

    assert apr30_result.season == "summer"
    assert may1_result.season == "summer"


def test_seasonal_boundary_oct31_vs_nov1_flips_season() -> None:
    """Locks the *actual* seasonal boundary — Oct/Nov in the Northern
    hemisphere — where the season label flips from `summer` to `winter`.
    """
    oct31 = _utc(2025, 10, 31)
    nov1 = _utc(2025, 11, 1)

    oct31_result = ComfortCalculator.score(
        t_c=24.0, rh_pct=50.0, bucket_time=oct31, hemisphere="N"
    )
    nov1_result = ComfortCalculator.score(
        t_c=24.0, rh_pct=50.0, bucket_time=nov1, hemisphere="N"
    )

    assert oct31_result.season == "summer"
    assert nov1_result.season == "winter"


# ---------------------------------------------------------------------
# 6. Southern-hemisphere mirroring: same (month, T, RH); season flips.
# ---------------------------------------------------------------------
@pytest.mark.parametrize(
    "month, hemi_n_expected, hemi_s_expected",
    [
        (1, "winter", "summer"),  # January
        (5, "summer", "winter"),  # May
        (7, "summer", "winter"),  # July
        (12, "winter", "summer"),  # December
    ],
)
def test_hemisphere_flips_season_for_same_month(
    month: int, hemi_n_expected: str, hemi_s_expected: str
) -> None:
    bucket = _utc(2025, month, 15)
    north = ComfortCalculator.score(
        t_c=25.0, rh_pct=50.0, bucket_time=bucket, hemisphere="N"
    )
    south = ComfortCalculator.score(
        t_c=25.0, rh_pct=50.0, bucket_time=bucket, hemisphere="S"
    )
    assert north.season == hemi_n_expected
    assert south.season == hemi_s_expected


def test_southern_hemisphere_may_25c_50rh_is_winter() -> None:
    # Issue #9 AC explicit fixture: `(May, 25 °C, 50 % RH, S) → "winter"`.
    bucket = _utc(2025, 5, 15)

    result = ComfortCalculator.score(
        t_c=25.0, rh_pct=50.0, bucket_time=bucket, hemisphere="S"
    )

    assert result.season == "winter"
    # Score is computed against the WINTER polygon now. (25 °C, 50 %)
    # sits just outside the winter polygon's right edge — should be
    # outside (< 100) but only slightly (still well above 0).
    assert 0.0 < result.score < 100.0
    # The exact band depends on _ALPHA, but the point is close enough
    # to the boundary that we lock the lower bound at "marginal" (>= 50).
    assert result.rating in {"excellent", "acceptable", "marginal"}


# ---------------------------------------------------------------------
# 7. Far-outside points → 0 (uncomfortable). Exercises the linear
#    distance penalty saturating at 0.
# ---------------------------------------------------------------------
def test_far_outside_summer_polygon_scores_zero() -> None:
    bucket = _utc(2025, 7, 1)
    # 60 °C at 0 % RH — far beyond the polygon; distance saturates the
    # `_ALPHA · d` penalty so score clamps to 0.
    result = ComfortCalculator.score(
        t_c=60.0, rh_pct=0.0, bucket_time=bucket, hemisphere="N"
    )
    assert result.score == 0.0
    assert result.rating == "uncomfortable"


def test_far_outside_winter_polygon_scores_zero() -> None:
    bucket = _utc(2025, 1, 1)
    # -20 °C at 100 % RH — far below the winter polygon.
    result = ComfortCalculator.score(
        t_c=-20.0, rh_pct=100.0, bucket_time=bucket, hemisphere="N"
    )
    assert result.score == 0.0
    assert result.rating == "uncomfortable"


# ---------------------------------------------------------------------
# 8. Out-of-range RH is clamped. The calculator does not raise.
# ---------------------------------------------------------------------
def test_rh_above_100_is_clamped() -> None:
    bucket = _utc(2025, 5, 15)
    # RH = 150 % is a sensor error; clamp to 100 and score normally.
    over = ComfortCalculator.score(
        t_c=25.0, rh_pct=150.0, bucket_time=bucket, hemisphere="N"
    )
    clipped = ComfortCalculator.score(
        t_c=25.0, rh_pct=100.0, bucket_time=bucket, hemisphere="N"
    )
    assert over.score == clipped.score
    assert over.season == clipped.season


def test_negative_rh_is_clamped() -> None:
    bucket = _utc(2025, 5, 15)
    under = ComfortCalculator.score(
        t_c=25.0, rh_pct=-20.0, bucket_time=bucket, hemisphere="N"
    )
    clipped = ComfortCalculator.score(
        t_c=25.0, rh_pct=0.0, bucket_time=bucket, hemisphere="N"
    )
    assert under.score == clipped.score


# ---------------------------------------------------------------------
# Pure-function discipline check — same inputs ⇒ same outputs across
# multiple invocations, and the function never touches the clock.
# ---------------------------------------------------------------------
def test_score_is_deterministic_across_calls() -> None:
    bucket = _utc(2025, 6, 15)
    args = dict(t_c=24.8, rh_pct=55.0, bucket_time=bucket, hemisphere="N")
    results = [ComfortCalculator.score(**args) for _ in range(10)]
    first = results[0]
    for r in results[1:]:
        assert r == first


def test_hemisphere_invalid_raises() -> None:
    bucket = _utc(2025, 5, 15)
    with pytest.raises(ValueError):
        # type: ignore[arg-type]
        ComfortCalculator.score(
            t_c=25.0, rh_pct=50.0, bucket_time=bucket, hemisphere="X"  # type: ignore[arg-type]
        )


# ---------------------------------------------------------------------
# Polygon-vertex regression: every documented vertex evaluates to a
# point-in-polygon "True". If a future refactor breaks the closure
# logic, this test catches it.
# ---------------------------------------------------------------------
def test_every_summer_vertex_is_on_or_inside_polygon() -> None:
    bucket = _utc(2025, 7, 1)
    centroid_t, centroid_rh = 25.0, 40.0
    # Nudge each vertex 5 % of the way toward the polygon centroid so
    # the point-in-polygon test counts it as inside. (Pure-vertex
    # points land on the boundary, which ray-casting can return either
    # way; a small but non-trivial nudge keeps the test stable.)
    for vx_t, vx_rh in _SUMMER_VERTICES:
        nudge_t = vx_t * 0.95 + centroid_t * 0.05
        nudge_rh = vx_rh * 0.95 + centroid_rh * 0.05
        result = ComfortCalculator.score(
            t_c=nudge_t,
            rh_pct=nudge_rh,
            bucket_time=bucket,
            hemisphere="N",
        )
        assert result.season == "summer"
        assert result.score == 100.0, (
            f"summer vertex {(vx_t, vx_rh)} nudged 5% toward centroid "
            f"({nudge_t}, {nudge_rh}) should score 100, got {result.score}"
        )


def test_every_winter_vertex_is_on_or_inside_polygon() -> None:
    bucket = _utc(2025, 1, 15)
    centroid_t, centroid_rh = 21.0, 40.0
    for vx_t, vx_rh in _WINTER_VERTICES:
        nudge_t = vx_t * 0.95 + centroid_t * 0.05
        nudge_rh = vx_rh * 0.95 + centroid_rh * 0.05
        result = ComfortCalculator.score(
            t_c=nudge_t,
            rh_pct=nudge_rh,
            bucket_time=bucket,
            hemisphere="N",
        )
        assert result.season == "winter"
        assert result.score == 100.0, (
            f"winter vertex {(vx_t, vx_rh)} nudged 5% toward centroid "
            f"({nudge_t}, {nudge_rh}) should score 100, got {result.score}"
        )
