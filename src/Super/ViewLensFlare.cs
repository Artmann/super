using System.Numerics;
using Rendition;

namespace Super;

/// <summary>
/// The lens flare a frame asks for, kept for <see cref="ViewLights"/>: the settings, and the lights named with
/// <see cref="ViewLights.FlareLight"/> on top of the settings' own. It reuses its arrays, so it allocates nothing.
/// </summary>
internal sealed class ViewLensFlare
{
    private readonly LensFlareLight[] _lights = new LensFlareLight[LensFlareSettings.MaxLights];
    private readonly LensFlareLight[] _added = new LensFlareLight[LensFlareSettings.MaxLights];
    private LensFlareSettings _settings;
    private int _addedCount;

    /// <summary>The settings, with their lights followed by the added ones.</summary>
    public LensFlareSettings Settings
    {
        get
        {
            if (_addedCount == 0)
            {
                return _settings;
            }

            int own = _settings.Lights.Length;
            _settings.Lights.Span.CopyTo(_lights);
            _added.AsSpan(0, _addedCount).CopyTo(_lights.AsSpan(own));
            return _settings with { Lights = _lights.AsMemory(0, own + _addedCount) };
        }
    }

    public void Set(in LensFlareSettings settings, string parameter)
    {
        LensFlareChecks.Settings(settings, _addedCount, parameter);
        _settings = settings;
    }

    public void Add(in LensFlareLight light, string parameter)
    {
        LensFlareChecks.Light(light, parameter);
        LensFlareChecks.Count(_settings.Lights.Length + _addedCount + 1, parameter);
        _added[_addedCount++] = light;
    }

    public void Clear()
    {
        _settings = default;
        _addedCount = 0;
    }
}
