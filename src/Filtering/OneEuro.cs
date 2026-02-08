using System;

public class OneEuroFilter
{
    private double _minCutoff;     // Minimum cutoff frequency
    private double _beta;          // Speed coefficient for dynamic cutoff
    private double _dCutoff;       // Derivative (d) cutoff frequency

    private bool _initialized;
    private double _prevValue;     // Previous filtered value
    private double _prevDerivative;// Previous filtered derivative
    private DateTime _lastTime;

    /// <summary>
    /// Create a 1€ filter for 1D signals.
    /// </summary>
    /// <param name="minCutoff">Minimal cutoff frequency (Hz)</param>
    /// <param name="beta">Speed coefficient to adjust cutoff based on derivative</param>
    /// <param name="dCutoff">Cutoff frequency for derivative filter</param>
    public OneEuroFilter(double minCutoff = 1.0, double beta = 0.0, double dCutoff = 1.0)
    {
        _minCutoff = minCutoff;
        _beta = beta;
        _dCutoff = dCutoff;
    }

    /// <summary>
    /// Filters an incoming raw value and returns the smoothed result.
    /// Call this whenever you have a new reading; dt is automatically inferred unless you want to pass it.
    /// </summary>
    public double Filter(double value, DateTime currentTime)
    {
        if (!_initialized)
        {
            // First call, just initialize
            _prevValue = value;
            _prevDerivative = 0.0;
            _lastTime = currentTime;
            _initialized = true;
            return value;
        }

        double dt = (currentTime - _lastTime).TotalSeconds;
        if (dt <= 0.0)
        {
            // If time hasn’t progressed, just return the last value
            return _prevValue;
        }

        // 1) Estimate derivative of the signal
        double rawDerivative = (value - _prevValue) / dt;
        // Filter the derivative with a cutoff freq = dCutoff
        double derivativeCutoff = _dCutoff;
        double derivativeAlpha = ComputeAlpha(derivativeCutoff, dt);
        double dFiltered = ExponentialSmooth(rawDerivative, _prevDerivative, derivativeAlpha);

        // 2) Compute cutoff frequency based on the current speed
        double cutoff = _minCutoff + _beta * Math.Abs(dFiltered);

        // 3) Filter the actual value
        double alpha = ComputeAlpha(cutoff, dt);
        double filteredValue = ExponentialSmooth(value, _prevValue, alpha);

        // Store results for next iteration
        _prevValue = filteredValue;
        _prevDerivative = dFiltered;
        _lastTime = currentTime;

        return filteredValue;
    }

    private static double ExponentialSmooth(double x, double xPrev, double alpha)
    {
        return alpha * x + (1.0 - alpha) * xPrev;
    }

    /// <summary>
    /// Standard 1€ formula for computing alpha given a cutoff frequency and time step.
    /// alpha = 2π·dt·cutoff / (2π·dt·cutoff + 1)
    /// </summary>
    private static double ComputeAlpha(double cutoffFreq, double dt)
    {
        double tau = 1.0 / (2.0 * Math.PI * cutoffFreq);
        return (float)(1.0 / (1.0 + tau / dt));
    }
}

