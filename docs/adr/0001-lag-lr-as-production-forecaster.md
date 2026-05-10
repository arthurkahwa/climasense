# 1. Lag-LR as the production forecaster

Date: 2026-05-08
Status: Accepted

## Context

The accompanying notebook (`Climate_Time_Series_Analysis.ipynb`) evaluated five forecasting approaches on a 14-day held-out test (336 hours, 89,903 training hours):

| Model | MAE (°C) | RMSE (°C) |
|---|---:|---:|
| **Linear regression on 8 lags + sin/cos hour/dow** | **0.214** | **0.293** |
| Gradient boosting (1-step) | 0.215 | 0.305 |
| LSTM (PyTorch) | 0.248 | 0.314 |
| Rolling 24h mean | 0.248 | 0.320 |
| 1D-CNN (PyTorch) | 0.266 | 0.340 |
| Naive (last value) | 0.217 | 0.370 |
| SARIMA(1,0,1)(1,0,1,24) | 0.344 | 0.442 |
| ARIMA(2,0,2) | 0.571 | 0.649 |

The original platform spec named ARIMA — the worst performer — as the production forecaster, in `models/forecaster.py` and the `/api/forecast` contract. This was a buzzword choice rather than an evidence-driven one.

## Decision

Production ships **lag-LR** (linear regression on 8 lag terms plus `sin/cos` encodings of hour-of-day and day-of-week) behind an `IForecaster` interface:

```python
class IForecaster(Protocol):
    def fit(self, history: pd.DataFrame) -> None: ...
    def predict(self, horizon_hours: int) -> ForecastBatch: ...
```

Default implementation: `LagLinearForecaster`. Optional implementations: `RollingMeanForecaster`, `ArimaForecaster`, `SarimaForecaster`, `LstmForecaster`. The `Forecasts.ModelVersion` column records which implementation produced each row.

The Explorer UI surfaces a leaderboard of the notebook's evaluation alongside the live model so reviewers see the evidence that produced this choice.

## Consequences

- The README's portfolio pitch shifts from "ARIMA forecasting" to "evidence-driven model selection". Stronger framing for engineering reviewers.
- The Class diagram changes: `Forecaster` becomes `IForecaster` + `LagLinearForecaster` (default) + four no-op stubs that read from notebook-evaluation rows for the leaderboard.
- Lag-LR re-fits in seconds on 90k rows; APScheduler can refit hourly without budget concerns.
- Adding a real ARIMA/LSTM live impl in future is one class implementing `IForecaster` plus a flag.
- The README must explicitly link the notebook leaderboard from the forecasting feature row.
