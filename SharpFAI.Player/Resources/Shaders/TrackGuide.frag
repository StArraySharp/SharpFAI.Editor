#version 330 core

in vec4 vColor;
in float vProfile;
in float vPhase;
flat in float vKind;
flat in float vProgress;

out vec4 color;

void main()
{
    float edge = abs(vProfile);
    if (vKind == 1.0)
    {
        vec3 tint = mix(vColor.rgb, vec3(0.84, 0.58, 0.97), 0.5 - 0.5 * cos(vPhase * 18.84956));
        float core = 1.0 - smoothstep(0.10, 0.25, edge);
        float haze = pow(max(0.0, 1.0 - edge), 2.0);
        float progress = 1.0 - smoothstep(vProgress - 0.015, vProgress + 0.015, vPhase);
        color = vec4(mix(tint, vec3(0.98, 0.97, 1.0), core),
            vColor.a * clamp(0.45 * haze + (0.81 + 0.19 * progress) * core, 0.0, 1.0));
    }
    else
    {
        color = vec4(vColor.rgb, vColor.a * (1.0 - smoothstep(0.68, 1.0, edge)));
    }
}
