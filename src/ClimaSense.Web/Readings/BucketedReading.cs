// SPDX-License-Identifier: MIT
//
// Wire DTOs for `GET /api/readings/range`. Shape matches the
// `BucketedReading` + `BucketedReadingsResponse` schemas in
// `contracts/openapi.yaml`. PascalCase properties → camelCase JSON
// via the global `JsonNamingPolicy.CamelCase` in Program.cs.
//
// Nullable metric fields encode "empty bucket" (no readings fell into
// the slot). The dashboard renders those as gaps in the time-series.

#nullable enable

using System;
using System.Collections.Generic;

namespace ClimaSense.Web.Readings;

/// <summary>
/// One bucket (or raw row) in a range-query response. For aggregated
/// buckets (Hour / Day / Week), <see cref="SampleCount"/> is the count
/// of readings that fell into the bucket. For <see cref="RangeBucket.Raw"/>,
/// every row is itself a bucket with <see cref="SampleCount"/> = 1 and
/// the mean / min / max fields are all the row's single value.
/// </summary>
/// <param name="BucketTime">Bucket start (UTC). For Raw, the row's ReadingTime.</param>
/// <param name="SampleCount">Number of rows in the bucket. 0 for empty.</param>
/// <param name="TemperatureMean">Mean temperature (°C). <c>null</c> on empty bucket.</param>
/// <param name="TemperatureMin">Min temperature (°C). <c>null</c> on empty bucket.</param>
/// <param name="TemperatureMax">Max temperature (°C). <c>null</c> on empty bucket.</param>
/// <param name="HumidityMean">Mean humidity (% RH). <c>null</c> on empty bucket.</param>
/// <param name="HumidityMin">Min humidity. <c>null</c> on empty bucket.</param>
/// <param name="HumidityMax">Max humidity. <c>null</c> on empty bucket.</param>
public sealed record BucketedReading(
    DateTime BucketTime,
    int SampleCount,
    double? TemperatureMean,
    double? TemperatureMin,
    double? TemperatureMax,
    double? HumidityMean,
    double? HumidityMin,
    double? HumidityMax);

/// <summary>
/// Response envelope for <c>GET /api/readings/range</c>. Echoes the
/// resolved window (post cursor-clip) plus the bucket granularity so
/// the dashboard never has to guess what it actually received.
/// </summary>
public sealed record BucketedReadingsResponse(
    DateTime Start,
    DateTime End,
    string Bucket,
    IReadOnlyList<BucketedReading> Buckets);
