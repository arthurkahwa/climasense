"""ComfortCalculator — ASHRAE 55-2020 graphical comfort zone (Figure 5.3.1).

Pure module. No DB access, no clock dependency, no IO. The function
`ComfortCalculator.score(t_c, rh_pct, bucket_time, hemisphere)` is the
single public entry point; given the same inputs it always returns the
same outputs.

Why pure:

  * Trivially testable — golden test 2
    (`test_comfort_polygons_seasonal_boundary`) hits known triplets
    on both polygons + the seasonal boundary + both hemispheres.
  * The scheduled job (`comfort_emitter.ComfortEmitter`) and the
    on-demand endpoint (`comfort_router`) compose the pure score
    with a SQL read of the trailing window — every IO concern lives
    outside this module.
  * Mirrors the pattern of the .NET `CursorSnapshot` and the Python
    cursor module: small, hand-rolled, no framework coupling.

Scoring shape (per ADR-0005):

  * Inside the polygon for the selected season: `score = 100`.
  * Outside the polygon: `max(0, 100 - α · d_polygon)`, where
    `d_polygon` is the Euclidean distance (in (T °C, RH %) space) from
    the test point to the polygon's boundary. `α` is a tuning constant
    chosen so the rating bands ("excellent" / "acceptable" / "marginal"
    / "uncomfortable" — see `_RATING_BANDS`) land at the documented
    score thresholds for plausible indoor (T, RH) inputs.

Season selection (per ADR-0005 amendment + issue #9 spec):

  * Northern hemisphere (`hemisphere == "N"`):
      - Summer: months 4..10 inclusive (Apr–Oct).
      - Winter: months 11, 12, 1, 2, 3 (Nov–Mar).
  * Southern hemisphere (`hemisphere == "S"`):
      - The same calendar months mapped through the mirror: summer
        becomes winter and vice-versa. So `(May, S) -> "winter"` and
        `(Jan, S) -> "summer"`.

Polygon vertices (psychrometric T °C × RH %):

  The ASHRAE 55-2020 graphical comfort zone (Figure 5.3.1) is bounded
  by lines of constant operative temperature and constant humidity
  ratio. We approximate operative temperature with air temperature
  (the dataset has no mean-radiant-temperature measurement — see
  ADR-0005 for why PMV/PPD is rejected).

  The vertex coordinates encoded below are derived from the standard's
  figure, then rounded to one decimal place in `T` and the nearest
  five percent in `RH`. They are stable across notebook regeneration
  because the standard itself is the source.

  Vertices are listed clockwise starting from the lower-left corner.
"""

from __future__ import annotations

import os
from dataclasses import dataclass
from datetime import datetime
from typing import Literal

Hemisphere = Literal["N", "S"]
Season = Literal["summer", "winter"]
Rating = Literal["excellent", "acceptable", "marginal", "uncomfortable"]


# ---------------------------------------------------------------------
# Polygon vertices for ASHRAE 55-2020 Figure 5.3.1.
#
# Listed clockwise from the lower-left corner so the centroid lies
# inside the polygon. Each vertex is `(T °C, RH %)`. The polygons are
# *closed* — the algorithm treats them as such (no need to repeat the
# first vertex at the end).
#
# Summer polygon (clo ≈ 0.5, met ≈ 1.2, still air):
#   The standard's summer zone spans ~24.0 °C to ~27.5 °C operative
#   temperature on the low-humidity edge, widening with humidity to
#   ~23.0 °C to ~26.0 °C at 80 % RH.
#
# Winter polygon (clo ≈ 1.0, met ≈ 1.2, still air):
#   The winter zone spans ~20.0 °C to ~23.5 °C on the low-humidity
#   edge, narrowing to ~19.5 °C to ~22.0 °C at 80 % RH.
# ---------------------------------------------------------------------
_SUMMER_VERTICES: tuple[tuple[float, float], ...] = (
    (24.5, 0.0),
    (27.5, 0.0),
    (26.5, 60.0),
    (25.5, 80.0),
    (22.5, 80.0),
    (23.5, 60.0),
)

_WINTER_VERTICES: tuple[tuple[float, float], ...] = (
    (20.0, 0.0),
    (23.5, 0.0),
    (23.0, 60.0),
    (22.0, 80.0),
    (19.0, 80.0),
    (19.5, 60.0),
)


# ---------------------------------------------------------------------
# Rating bands. The thresholds map [0, 100] → one of four labels.
# Inclusive on the lower bound; aligns with ADR-0005:
#   excellent     90+
#   acceptable    70..89
#   marginal      50..69
#   uncomfortable <50
# ---------------------------------------------------------------------
_RATING_BANDS: tuple[tuple[float, Rating], ...] = (
    (90.0, "excellent"),
    (70.0, "acceptable"),
    (50.0, "marginal"),
    (0.0, "uncomfortable"),
)


# ---------------------------------------------------------------------
# Scoring constants.
#
# `_ALPHA` is the linear penalty applied per unit of Euclidean distance
# (in (T °C, RH %) space) outside the polygon. 4.0 is chosen so a
# distance of 5 units (e.g. 1 °C off in T plus ~5 %RH off in RH,
# roughly) lands in the "acceptable" band (~80) and a distance of 25
# units saturates to 0 ("uncomfortable").
# ---------------------------------------------------------------------
_ALPHA: float = 4.0


# ---------------------------------------------------------------------
# Hemisphere / season lookup. The Northern-hemisphere months that select
# summer; the complement selects winter. Southern hemisphere mirrors.
# ---------------------------------------------------------------------
_NORTHERN_SUMMER_MONTHS = frozenset({4, 5, 6, 7, 8, 9, 10})


@dataclass(frozen=True, slots=True)
class ComfortScoreResult:
    """Pure output of `ComfortCalculator.score`.

    Mirrors the `ComfortScoreResponse` wire shape in
    `contracts/openapi.yaml` minus the `bucketTime` /
    `averageTemperature` / `averageHumidity` fields, which are added
    by the caller (router / emitter) since they're context, not
    polygon math.
    """

    score: float
    rating: Rating
    season: Season


# ---------------------------------------------------------------------
# Public API.
# ---------------------------------------------------------------------
class ComfortCalculator:
    """Pure ASHRAE 55 graphical-zone scorer.

    All methods are `@staticmethod`s — there is no instance state. The
    class exists as a namespace so callers spell `ComfortCalculator.score`
    rather than relying on a free function with a generic name.
    """

    @staticmethod
    def select_season(bucket_time: datetime, hemisphere: Hemisphere = "N") -> Season:
        """Return the season label for `bucket_time` under `hemisphere`.

        Pure: month-of-year + hemisphere → season. No clock read.
        """
        if hemisphere not in ("N", "S"):
            raise ValueError(
                f"hemisphere must be 'N' or 'S' (got {hemisphere!r})"
            )
        month = bucket_time.month
        is_northern_summer = month in _NORTHERN_SUMMER_MONTHS
        if hemisphere == "N":
            return "summer" if is_northern_summer else "winter"
        # Southern hemisphere mirrors: summer↔winter.
        return "winter" if is_northern_summer else "summer"

    @staticmethod
    def score(
        t_c: float,
        rh_pct: float,
        bucket_time: datetime,
        hemisphere: Hemisphere = "N",
    ) -> ComfortScoreResult:
        """Score a single (T, RH) point against the ASHRAE polygon for
        the season selected from `bucket_time` and `hemisphere`.

        Inputs:
          * `t_c`         — air temperature in °C.
          * `rh_pct`      — relative humidity as a percent (0..100).
                            Values outside [0, 100] are clamped; the
                            calculator does not raise.
          * `bucket_time` — used purely for season selection (calendar
                            month). The `datetime` itself doesn't enter
                            the scoring math.
          * `hemisphere`  — `"N"` (default) or `"S"`.

        Returns: `ComfortScoreResult(score, rating, season)`. `score`
        is clamped to `[0, 100]`. `rating` is the band label that
        corresponds to the score.
        """
        season = ComfortCalculator.select_season(bucket_time, hemisphere)
        polygon = _SUMMER_VERTICES if season == "summer" else _WINTER_VERTICES

        # Clamp RH to [0, 100] — out-of-range humidity is a sensor
        # error, not a contract violation. Temperature is left
        # unbounded (a reading of -40 °C is still scored, just very
        # far outside the polygon).
        rh = max(0.0, min(100.0, float(rh_pct)))
        point = (float(t_c), rh)

        inside = _point_in_polygon(point, polygon)
        if inside:
            raw_score = 100.0
        else:
            d = _min_distance_to_polygon_edge(point, polygon)
            raw_score = max(0.0, 100.0 - _ALPHA * d)

        # Clamp to [0, 100]. `inside == True` already gives 100; this
        # is defensive against future tuning of `_ALPHA`.
        score = max(0.0, min(100.0, raw_score))
        rating = _rating_for(score)
        return ComfortScoreResult(score=score, rating=rating, season=season)


# ---------------------------------------------------------------------
# Hemisphere resolver — reads `COMFORT_HEMISPHERE` from the environment.
# Lives here (not in `main.py`) so the comfort job + the on-demand
# endpoint share one resolution path. Default is `"N"`.
# ---------------------------------------------------------------------
def hemisphere_from_env() -> Hemisphere:
    """Read `COMFORT_HEMISPHERE` from the environment. Default `"N"`.

    Accepts (case-insensitively): `N`, `North`, `Northern`,
    `S`, `South`, `Southern`. Anything else is treated as `"N"`
    with no warning — the env var is a deployment switch, not a
    user-facing input.
    """
    raw = os.environ.get("COMFORT_HEMISPHERE", "N").strip().lower()
    if raw in ("s", "south", "southern"):
        return "S"
    return "N"


# =====================================================================
# Internals — point-in-polygon + distance-to-edge.
#
# Both helpers are pure functions over the same `(T, RH)` 2D plane the
# polygon vertices live in. No external dependencies beyond the stdlib.
# =====================================================================
def _point_in_polygon(
    point: tuple[float, float],
    polygon: tuple[tuple[float, float], ...],
) -> bool:
    """Return True iff `point` lies inside (or on the boundary of)
    the closed polygon. Standard ray-casting algorithm.

    The polygon is closed implicitly — we wrap from vertex `n-1` back
    to vertex `0`. Points exactly on a horizontal edge count as inside.
    """
    x, y = point
    n = len(polygon)
    inside = False
    j = n - 1
    for i in range(n):
        xi, yi = polygon[i]
        xj, yj = polygon[j]
        # Standard ray cast — a horizontal ray from `point` to +x.
        # The edge crosses the ray iff the y-bounds straddle `y`.
        if (yi > y) != (yj > y):
            # Compute the x at which the edge crosses y == point.y.
            slope = (xj - xi) / (yj - yi) if (yj - yi) != 0.0 else 0.0
            xcross = xi + (y - yi) * slope
            if x < xcross:
                inside = not inside
        j = i
    return inside


def _min_distance_to_polygon_edge(
    point: tuple[float, float],
    polygon: tuple[tuple[float, float], ...],
) -> float:
    """Return the minimum Euclidean distance from `point` to any edge
    of the closed polygon.

    Distance is computed in the raw (T, RH) plane; we do NOT rescale
    the axes. This is deliberate — the polygon vertices are in the
    same units, and rescaling would silently change the score's
    sensitivity to humidity vs temperature.
    """
    n = len(polygon)
    best = float("inf")
    for i in range(n):
        a = polygon[i]
        b = polygon[(i + 1) % n]
        d = _distance_point_to_segment(point, a, b)
        if d < best:
            best = d
    return best


def _distance_point_to_segment(
    point: tuple[float, float],
    a: tuple[float, float],
    b: tuple[float, float],
) -> float:
    """Euclidean distance from `point` to the line segment `(a, b)`."""
    px, py = point
    ax, ay = a
    bx, by = b
    dx = bx - ax
    dy = by - ay
    seg_len_sq = dx * dx + dy * dy
    if seg_len_sq == 0.0:
        # Degenerate segment — `a` and `b` coincide.
        return ((px - ax) ** 2 + (py - ay) ** 2) ** 0.5
    # Project `point` onto the segment, clamped to [0, 1].
    t = ((px - ax) * dx + (py - ay) * dy) / seg_len_sq
    if t < 0.0:
        t = 0.0
    elif t > 1.0:
        t = 1.0
    cx = ax + t * dx
    cy = ay + t * dy
    return ((px - cx) ** 2 + (py - cy) ** 2) ** 0.5


def _rating_for(score: float) -> Rating:
    """Map a numeric score to its rating-band label."""
    for threshold, label in _RATING_BANDS:
        if score >= threshold:
            return label
    return "uncomfortable"


__all__ = [
    "ComfortCalculator",
    "ComfortScoreResult",
    "Hemisphere",
    "Rating",
    "Season",
    "hemisphere_from_env",
]
