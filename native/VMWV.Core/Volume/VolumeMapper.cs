namespace VMWV.Core.Volume;

public static class VolumeMapper
{
    public static bool IsValidRange(double minimum, double maximum) =>
        double.IsFinite(minimum) && double.IsFinite(maximum)
        && minimum >= -60 && maximum <= 12 && minimum <= maximum;

    public static double ToVoicemeeterGain(
        int windowsVolume,
        double gainMin,
        double gainMax,
        bool limitMaxGainToZero,
        bool useLinearScale)
    {
        var normalizedVolume = Math.Clamp(windowsVolume, 0, 100);
        if (!IsValidRange(gainMin, gainMax))
            throw new ArgumentOutOfRangeException(nameof(gainMin), "Gain limits must be finite, ordered and between -60 and 12 dB.");
        var effectiveGainMax = limitMaxGainToZero ? Math.Min(0, gainMax) : gainMax;
        var effectiveGainMin = Math.Min(gainMin, effectiveGainMax);

        var gain = useLinearScale
            ? ToLinearGain(normalizedVolume, effectiveGainMin, effectiveGainMax)
            : ToLogarithmicGain(normalizedVolume, effectiveGainMin, effectiveGainMax);

        return Math.Clamp(Math.Round(gain, 1, MidpointRounding.AwayFromZero), effectiveGainMin, effectiveGainMax);
    }

    private static double ToLinearGain(int windowsVolume, double gainMin, double gainMax) =>
        windowsVolume * (gainMax - gainMin) / 100 + gainMin;

    private static double ToLogarithmicGain(int windowsVolume, double gainMin, double gainMax)
    {
        if (windowsVolume <= 0)
        {
            return gainMin;
        }

        var amplitude = Math.Log10(windowsVolume / 100d);
        return Math.Max(20 * amplitude + gainMax, gainMin);
    }
}
