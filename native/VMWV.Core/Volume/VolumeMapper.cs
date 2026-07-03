namespace VMWV.Core.Volume;

public static class VolumeMapper
{
    public static double ToVoicemeeterGain(
        int windowsVolume,
        double gainMin,
        double gainMax,
        bool limitMaxGainToZero,
        bool useLinearScale)
    {
        var normalizedVolume = Math.Clamp(windowsVolume, 0, 100);
        var effectiveGainMax = limitMaxGainToZero ? 0 : gainMax;

        var gain = useLinearScale
            ? ToLinearGain(normalizedVolume, gainMin, effectiveGainMax)
            : ToLogarithmicGain(normalizedVolume, gainMin, effectiveGainMax);

        return Math.Round(gain, 1, MidpointRounding.AwayFromZero);
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
