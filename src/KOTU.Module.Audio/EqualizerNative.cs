using LibVLCSharp.Shared;

namespace KOTU.Module.Audio;

/// <summary>A437: 호출자는 Audio Worker. UI에서 네이티브 EQ를 읽거나 적용하지 않는다.</summary>
internal static class EqualizerNative
{
    internal static EqualizerCatalog? ReadCatalog()
    {
        try
        {
            using var metadata = new Equalizer();
            var frequencies = new float[metadata.BandCount];
            for (var band = 0; band < frequencies.Length; band++)
                frequencies[band] = metadata.BandFrequency((uint)band);
            var presets = new List<EqualizerPreset>();
            for (var index = 0u; index < metadata.PresetCount; index++)
            {
                using var native = new Equalizer(index);
                var gains = new float[frequencies.Length];
                for (var band = 0; band < gains.Length; band++) gains[band] = native.Amp((uint)band);
                presets.Add(new EqualizerPreset(metadata.PresetName(index) ?? string.Empty, new EqualizerSnapshot(native.Preamp, gains)));
            }
            return new EqualizerCatalog(frequencies, presets);
        }
        catch { return null; } // 재생은 계속한다. 읽지 못한 밴드·프리셋을 UI에서 만들지 않는다.
    }

    internal static EqualizerApplyResult Apply(MediaPlayer player, EqualizerSnapshot? values, Func<bool> current)
    {
        if (!current()) return EqualizerApplyResult.Superseded;
        try
        {
            if (values is null)
                return player.UnsetEqualizer() ? EqualizerApplyResult.Applied : EqualizerApplyResult.Failed;
            using var native = new Equalizer();
            if (!native.SetPreamp(values.Preamp)) throw new InvalidOperationException("Equalizer preamp was rejected.");
            for (var band = 0; band < values.BandCount; band++)
                if (!native.SetAmp(values[band], (uint)band)) throw new InvalidOperationException("Equalizer band was rejected.");
            if (!current()) return EqualizerApplyResult.Superseded;
            if (player.SetEqualizer(native)) return EqualizerApplyResult.Applied;
        }
        catch { /* 실패는 아래 Off 복구로 격리한다. 재생 자체는 중단하지 않는다. */ }

        if (!current()) return EqualizerApplyResult.Superseded;
        try { if (player.UnsetEqualizer()) return EqualizerApplyResult.DisabledAfterFailure; }
        catch { /* Off 복구도 실패한 경우에는 상태를 단정하지 않는다. */ }
        return EqualizerApplyResult.Failed;
    }
}
