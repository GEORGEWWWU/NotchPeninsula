namespace NotchPeninsula;

internal readonly record struct IslandOutline(float TopX, float TopY, float Bottom)
{
    internal const float CapsuleOffset = 12f;
    internal float Flare => Math.Max(0f, -TopX);

    internal static IslandOutline Create(float height, float style)
    {
        float radius = Math.Min(height / 2f, 20f);
        return new(
            -Renderer.OUTER_R * (1f - style) + radius * style,
            Renderer.OUTER_R * (1f - style) + radius * style,
            Renderer.NOTCH_BOTTOM_RADIUS * (1f - style) + radius * style);
    }
}
