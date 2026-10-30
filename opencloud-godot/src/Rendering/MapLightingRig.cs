using Godot;
using OpenCloud.Assets.Maps;
using NVector3 = System.Numerics.Vector3;

namespace OpenCloud.Rendering;

/// <summary>
/// Applies a map lighting set (<see cref="MapLightingSet"/>) the way <c>UpDateMapInfo</c> hands
/// it to the renderer: background colour, ambient, up to four directional lights and depth fog.
/// Light and ambient colours use the GS scale (128 = 1.0); background and fog colours are
/// 0-255. A light's direction points from the surface towards the light (<c>GetSunPoint</c>
/// gives the noon sun +Y), so the Godot light shines the opposite way. Point lights of the set
/// are not applied yet.
/// </summary>
public partial class MapLightingRig : Node3D
{
    private readonly DirectionalLight3D[] _lights = new DirectionalLight3D[MapLightingSet.MaxLights];

    public override void _Ready()
    {
        for (int i = 0; i < _lights.Length; i++)
        {
            _lights[i] = new DirectionalLight3D
            {
                Name = $"Light{i}",
                Visible = false,
                // Pieces with LIGHT_FLAG sit on another render layer and only get the ambient.
                LightCullMask = ~MapLoader.NoLightRenderLayer,
                ShadowEnabled = false,
            };
            AddChild(_lights[i]);
        }
    }

    public void Apply(MapLightingSet? set, Godot.Environment environment)
    {
        if (set == null)
        {
            foreach (var light in _lights)
                light.Visible = false;
            environment.FogEnabled = false;
            return;
        }

        environment.BackgroundMode = Godot.Environment.BGMode.Color;
        environment.BackgroundColor = Bytes(set.BackgroundColour);
        environment.AmbientLightSource = Godot.Environment.AmbientSource.Color;
        environment.AmbientLightColor = Gs(set.Ambient);
        environment.AmbientLightEnergy = 1.0f;

        for (int i = 0; i < _lights.Length; i++)
        {
            var light = _lights[i];
            if (set.Lights[i] is not { } source || source.Direction.LengthSquared() < 1e-8f || source.Colour == NVector3.Zero)
            {
                light.Visible = false;
                continue;
            }
            var towards = new Vector3(source.Direction.X, source.Direction.Y, source.Direction.Z).Normalized();
            var up = Mathf.Abs(towards.Dot(Vector3.Up)) > 0.99f ? Vector3.Right : Vector3.Up;
            // A DirectionalLight3D shines along its -Z axis.
            light.Basis = Basis.LookingAt(-towards, up);
            light.LightColor = Gs(source.Colour);
            light.LightEnergy = 1.0f;
            light.Visible = true;
        }

        // mgFOG_PARAM: fog value 255 is clear, 0 is full fog colour.
        environment.FogEnabled = set.FogEnable && set.FogFar > set.FogNear;
        if (environment.FogEnabled)
        {
            environment.FogMode = Godot.Environment.FogModeEnum.Depth;
            environment.FogLightColor = Bytes(set.FogColour);
            environment.FogLightEnergy = 1.0f;
            environment.FogDensity = Mathf.Clamp(1.0f - set.FogFarValue / 255.0f, 0.0f, 1.0f);
            environment.FogDepthBegin = set.FogNear;
            environment.FogDepthEnd = set.FogFar;
            environment.FogDepthCurve = 1.0f;
            environment.FogSkyAffect = 0.0f;
        }
    }

    private static Color Gs(NVector3 c) =>
        new(Mathf.Clamp(c.X / 128.0f, 0, 2), Mathf.Clamp(c.Y / 128.0f, 0, 2), Mathf.Clamp(c.Z / 128.0f, 0, 2));

    private static Color Bytes(NVector3 c) =>
        new(Mathf.Clamp(c.X / 255.0f, 0, 1), Mathf.Clamp(c.Y / 255.0f, 0, 1), Mathf.Clamp(c.Z / 255.0f, 0, 1));
}
