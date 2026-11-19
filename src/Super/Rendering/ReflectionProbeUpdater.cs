using Rendition;

namespace Super;

/// <summary>Keeps the renderer's reflection probes in step with the view's, and asks for their pictures when they're due.</summary>
internal sealed class ReflectionProbeUpdater(Renderer renderer)
{
    private readonly ReflectionProbeSchedule _schedule = new();

    /// <summary>The reflection probes to keep: the view's, or none while reflections are off.</summary>
    internal static ReadOnlySpan<ReflectionProbeRequest> WantedReflectionProbes(View view, RenderEffects off) =>
        off.HasFlag(RenderEffects.Reflections) ? [] : view.Indirect.ReflectionProbes;

    /// <summary>
    /// Puts each of the view's reflection probes in the renderer's line for a picture the first frame it's placed,
    /// again when it moves, and otherwise as its <see cref="ReflectionProbeRequest.Update"/> says. Moving the camera
    /// isn't a lighting change. A probe that's gone is removed alone, and the others keep their pictures.
    /// </summary>
    public void Update(View view, in FrameView frame, RenderEffects off)
    {
        var wanted = WantedReflectionProbes(view, off);
        foreach (int slot in _schedule.Forget(wanted))
        {
            renderer.Reflections.Remove(new ReflectionProbeHandle(slot));
        }

        if (wanted.IsEmpty)
        {
            return;
        }

        int lighting = renderer.Reflections.LightingHash(frame);
        foreach (var request in wanted)
        {
            int slot = _schedule.SlotOf(request.Key);
            int hash = HashCode.Combine(lighting, RendererReflections.LightsHash(frame.LocalLights, request.Min, request.Max));
            bool waiting = slot >= 0 && renderer.Reflections.Waiting(new ReflectionProbeHandle(slot));
            if (!_schedule.ShouldCapture(request, hash, waiting))
            {
                continue;
            }

            // The schedule hears of the request only once the renderer took it, so a throw leaves the two in step.
            if (slot >= 0)
            {
                renderer.Reflections.Update(new ReflectionProbeHandle(slot), request.ToProbe());
            }
            else
            {
                slot = renderer.Reflections.Add(request.ToProbe()).Index;
            }

            _schedule.Asked(request, hash, slot);
        }
    }
}
