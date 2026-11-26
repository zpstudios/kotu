using System.Text.Json;

namespace KOTU.Module.Audio;

/// <summary>A437: 네이티브 핸들과 분리된 불변 값. 프리셋·대기 요청·저장이 배열을 공유하지 않는다.</summary>
internal sealed class EqualizerSnapshot
{
    // LibVLCSharp 3.10.0 Equalizer.SetAmp/SetPreamp 및 libvlc 3.x의 공개 계약.
    internal const float MinimumDb = -20;
    internal const float MaximumDb = 20;
    private readonly float[] _gains;
    internal float Preamp { get; }
    internal int BandCount => _gains.Length;
    internal float this[int band] => _gains[band];

    internal EqualizerSnapshot(float preamp, IEnumerable<float> gains)
    {
        _gains = gains.ToArray();
        if (!ValidGain(preamp) || _gains.Length == 0 || _gains.Any(value => !ValidGain(value)))
            throw new ArgumentException("Invalid equalizer values.");
        Preamp = preamp;
    }

    internal static bool ValidGain(float value) => float.IsFinite(value) && value >= MinimumDb && value <= MaximumDb;
    internal static EqualizerSnapshot Flat(int count) => new(0, new float[count]);
    internal float[] CopyGains() => (float[])_gains.Clone();

    internal EqualizerSnapshot WithBand(int band, float value)
    {
        var gains = CopyGains();
        gains[band] = value;
        return new EqualizerSnapshot(Preamp, gains);
    }
}

internal sealed record EqualizerPreset(string Name, EqualizerSnapshot Values);

/// <summary>워커에서 읽은 주파수와 프리셋 값. UI는 이 관리 메모리만 읽는다.</summary>
internal sealed class EqualizerCatalog
{
    internal IReadOnlyList<float> Frequencies { get; }
    internal IReadOnlyList<EqualizerPreset> Presets { get; }

    internal EqualizerCatalog(IEnumerable<float> frequencies, IEnumerable<EqualizerPreset> presets)
    {
        var bands = frequencies.ToArray();
        var choices = presets.ToArray();
        if (bands.Length == 0 || bands.Any(value => !float.IsFinite(value) || value <= 0) ||
            choices.Any(p => string.IsNullOrWhiteSpace(p.Name) || p.Name.Any(char.IsControl) || p.Values.BandCount != bands.Length) ||
            choices.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != choices.Length)
            throw new ArgumentException("Invalid equalizer metadata.");
        Frequencies = Array.AsReadOnly(bands);
        Presets = Array.AsReadOnly(choices);
    }

    internal EqualizerSnapshot? Find(string name) => Presets.FirstOrDefault(p => p.Name == name)?.Values;
}

/// <summary>창별 선택 + 마지막 Custom. 기존 프리셋 이름과 빈 문자열 Off를 그대로 읽고 쓴다.</summary>
internal sealed class EqualizerState
{
    internal const string SelectionKey = "audio.equalizer";
    internal const string CustomKey = "audio.equalizer.custom.v1";
    // 네이티브 표시 이름에 없는 제어 문자 접두어로 Custom 표시 문자열과도 충돌하지 않는다.
    internal const string CustomSelection = "\u001fKOTU.Audio.Equalizer.Custom.v1";
    private string _savedCustom;
    private bool _customDirty;
    internal EqualizerCatalog? Catalog { get; private set; }
    internal string Selection { get; private set; }
    internal EqualizerSnapshot? Custom { get; private set; }
    internal EqualizerSnapshot? Current => Selection == CustomSelection ? Custom : Catalog?.Find(Selection);

    internal EqualizerState(string? selection, string? savedCustom)
    {
        Selection = selection ?? string.Empty;
        _savedCustom = savedCustom ?? string.Empty;
    }

    // 메타데이터를 못 읽은 동안에는 저장한 밴드 수를 추측하지 않으며 Off만 노출한다.
    internal bool SetCatalog(EqualizerCatalog catalog)
    {
        Catalog = catalog;
        Custom = ReadCustom(_savedCustom, catalog.Frequencies.Count);
        var valid = Selection.Length == 0 || (Selection == CustomSelection ? Custom is not null : catalog.Find(Selection) is not null);
        if (!valid) Selection = string.Empty;
        return valid;
    }

    internal bool Select(string selection)
    {
        if (selection.Length == 0) { Selection = selection; return true; }
        if (Catalog is null) return false;
        if (selection == CustomSelection)
        {
            Custom ??= EqualizerSnapshot.Flat(Catalog.Frequencies.Count);
            // 명시적으로 선택한 창의 소리와 다음 실행의 복원값을 맞춘다.
            _customDirty = true;
        }
        else if (Catalog.Find(selection) is null) return false;
        Selection = selection;
        return true;
    }

    internal void SetBand(int band, float value)
    {
        if (Catalog is null || !EqualizerSnapshot.ValidGain(value) || band < 0 || band >= Catalog.Frequencies.Count)
            return;
        var basis = Current ?? Custom ?? EqualizerSnapshot.Flat(Catalog.Frequencies.Count);
        if (Selection == CustomSelection && basis[band] == value) return;
        Custom = basis.WithBand(band, value);
        Selection = CustomSelection;
        _customDirty = true;
    }

    internal void Reset()
    {
        if (Catalog is null) return;
        Custom = EqualizerSnapshot.Flat(Catalog.Frequencies.Count);
        Selection = CustomSelection;
        _customDirty = true;
    }

    // 다른 창의 Off/프리셋 전환이 오래된 Custom으로 공유 설정을 덮지 않게 한다.
    // 아직 저장하지 않은 직접 편집은 곧바로 Off로 전환해도 함께 보존한다.
    internal void WriteSettings(Action<string, string> write)
    {
        write(SelectionKey, Selection);
        if (!_customDirty) return;
        write(CustomKey, SerializeCustom());
        _customDirty = false;
    }

    // 설정 서비스에 넘기는 값은 직렬화된 불변 문자열. 외부 배열 수정과 지연 저장이 경합하지 않는다.
    internal string SerializeCustom() => Custom is null ? _savedCustom :
        _savedCustom = JsonSerializer.Serialize(new StoredCustom(1, Custom.Preamp, Custom.CopyGains()));

    private static EqualizerSnapshot? ReadCustom(string json, int bandCount)
    {
        try
        {
            var value = JsonSerializer.Deserialize<StoredCustom>(json);
            if (value is null || value.Version != 1 || value.Gains?.Length != bandCount) return null;
            return new EqualizerSnapshot(value.Preamp, value.Gains);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or NotSupportedException) { return null; }
    }

    private sealed record StoredCustom(int Version, float Preamp, float[]? Gains);
}
