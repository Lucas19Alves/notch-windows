namespace NotchApp;

// Mola amortecida, como as animações do iOS: muda de alvo no meio do caminho sem perder a velocidade.
public sealed class Spring(double value, double stiffness = 260, double damping = 24)
{
    public double Value = value;
    public double Velocity;
    public double Target = value;

    public bool Settled => Math.Abs(Target - Value) < 0.05 && Math.Abs(Velocity) < 0.05;

    public void Step(double dt)
    {
        const double h = 1 / 240.0; // passos pequenos mantêm a integração estável em qualquer taxa de quadros
        for (var t = 0.0; t < dt; t += h)
        {
            var step = Math.Min(h, dt - t);
            Velocity += (-stiffness * (Value - Target) - damping * Velocity) * step;
            Value += Velocity * step;
        }
        if (Settled) Snap();
    }

    public void Snap()
    {
        Value = Target;
        Velocity = 0;
    }
}
