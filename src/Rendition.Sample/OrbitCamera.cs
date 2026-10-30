using System.Numerics;

/// <summary>A camera that circles a target: drag to orbit, scroll to zoom.</summary>
internal struct OrbitCamera
{
    private const float OrbitSpeed = 0.006f;
    private const float ZoomStep = 0.9f;

    public Vector3 Target;
    public float Yaw;
    public float Pitch;
    public float Distance;

    public readonly Vector3 Position => Target + Distance * new Vector3(
        MathF.Sin(Yaw) * MathF.Cos(Pitch),
        MathF.Sin(Pitch),
        MathF.Cos(Yaw) * MathF.Cos(Pitch));

    public readonly Matrix4x4 View => Matrix4x4.CreateLookAt(Position, Target, Vector3.UnitY);

    public void Orbit(float deltaX, float deltaY)
    {
        Yaw -= deltaX * OrbitSpeed;
        Pitch = Math.Clamp(Pitch + deltaY * OrbitSpeed, -1.4f, 1.4f);
    }

    public void Zoom(float wheelSteps) => Distance = Math.Clamp(Distance * MathF.Pow(ZoomStep, wheelSteps), 0.3f, 100f);
}
