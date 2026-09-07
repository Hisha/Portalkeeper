using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;

namespace Portalkeeper.Services.CharacterRendering;

internal sealed class ClientLighting
{
    private Vector3 _ambient;
    private readonly List<(Vector3 Direction, Vector3 Color)> _lights = new();

    // A consistent portrait light rig, taken from the installed client's Human
    // character-selection scene (the supplied reference). No Lua is executed.
    public static ClientLighting Load(ClientAssets assets)
    {
        var result = new ClientLighting();
        const string path = @"Interface\GlueXML\GlueParent.lua";
        if (assets.Exists(path))
        {
            string source = Encoding.UTF8.GetString(assets.Read(path));
            var section = Regex.Match(source, @"\bHUMAN\s*=\s*\{(?<rows>(?:\s*\{[^{}]*\}\s*,?)+)\s*\}");
            foreach (Match row in Regex.Matches(section.Groups["rows"].Value, @"\{([^{}]*)\}"))
            {
                var fields = row.Groups[1].Value.Split(',');
                if (fields.Length != 13) continue;
                var values = new float[13];
                bool valid = true;
                for (int i = 0; i < values.Length; ++i)
                    valid &= float.TryParse(fields[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]) && float.IsFinite(values[i]);
                if (!valid || values[0] != 1 || values[1] != 0) continue;
                result._ambient += values[5] * new Vector3(values[6], values[7], values[8]);
                var direction = -new Vector3(values[2], values[3], values[4]);
                if (direction.LengthSquared() > 1e-8f)
                    result._lights.Add((Vector3.Normalize(direction), values[9] * new Vector3(values[10], values[11], values[12])));
            }
        }
        if (result._lights.Count == 0)
        {
            // Preserve the old neutral lighting if this optional UI resource is absent.
            result._ambient = new Vector3(.55f);
            result._lights.Add((Vector3.Normalize(new Vector3(.7f, -.4f, .8f)), new Vector3(.45f)));
        }
        return result;
    }

    public Vector3 Shade(Vector3 normal)
    {
        var color = _ambient;
        foreach (var light in _lights) color += light.Color * MathF.Max(Vector3.Dot(normal, light.Direction), 0);
        return Vector3.Clamp(color, Vector3.Zero, Vector3.One);
    }
}
