namespace NotchApp;

// Modo de energia do Windows 11 (o mesmo de Configurações > Energia). Vale para a fonte atual: bateria ou tomada.
static class PowerModes
{
    public enum Mode { Efficiency, Balanced, Performance }

    static readonly Dictionary<Mode, Guid> Schemes = new()
    {
        [Mode.Efficiency] = new("961CC777-2547-4F9D-8174-7D86181B8A7A"),
        [Mode.Balanced] = Guid.Empty,
        [Mode.Performance] = new("DED574B5-45A0-4F42-8737-46345C09C238"),
    };

    // null: o plano ativo não é o Equilibrado do Windows, e aí os modos não existem
    public static Mode? Current
    {
        get
        {
            try
            {
                if (Native.PowerGetEffectiveOverlayScheme(out var g) != 0) return null;
                foreach (var (mode, scheme) in Schemes)
                    if (scheme == g) return mode;
                return Mode.Balanced; // modos antigos ("melhor desempenho" do Windows 10) contam como o do meio
            }
            catch { return null; }
        }
    }

    public static bool Set(Mode mode)
    {
        try { return Native.PowerSetActiveOverlayScheme(Schemes[mode]) == 0; }
        catch { return false; }
    }
}
