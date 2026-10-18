using System;

namespace SpotifyNotch;

/// <summary>Damped spring used for the notch width/height animation.</summary>
public struct Spring
{
    public double Value, Velocity, Target;

    public void Snap(double v)
    {
        Value = v;
        Target = v;
        Velocity = 0;
    }

    public readonly bool IsSettled => Math.Abs(Value - Target) < 0.15 && Math.Abs(Velocity) < 2;

    /// <summary>Lock exactly onto the target once the spring has effectively stopped.</summary>
    public void SettleIfClose()
    {
        if (!IsSettled) return;
        Value = Target;
        Velocity = 0;
    }

    public void Step(double dt, double stiffness, double damping)
    {
        int steps = Math.Max(1, (int)Math.Ceiling(dt / 0.004));
        double h = dt / steps;
        for (int i = 0; i < steps; i++)
        {
            double accel = -stiffness * (Value - Target) - damping * Velocity;
            Velocity += accel * h;
            Value += Velocity * h;
        }
    }
}
