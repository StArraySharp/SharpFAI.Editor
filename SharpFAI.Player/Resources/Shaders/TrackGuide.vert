#version 330 core

layout(location = 0) in vec2 aPreviewPosition;
layout(location = 1) in vec2 aPlaybackPosition;
layout(location = 2) in vec2 aProfile;
layout(location = 3) in vec4 aTiming;
layout(location = 4) in vec4 aColor;
layout(location = 5) in vec2 aOpacity;
layout(location = 6) in vec2 aCueOffset;

uniform mat4 uViewProjection;
uniform bool uPlaying;
uniform float uTime;

out vec4 vColor;
out float vProfile;
out float vPhase;
flat out float vKind;
flat out float vProgress;

void main()
{
    vec2 position = uPlaying ? aPlaybackPosition : aPreviewPosition;
    vColor = aColor;
    vColor.a *= uPlaying ? aOpacity.y : aOpacity.x;
    vKind = aTiming.w;
    vPhase = aTiming.x;
    vProfile = aProfile.x;
    vProgress = uPlaying ? clamp((uTime - aTiming.y) / max(aTiming.z - aTiming.y, 0.0001), 0.0, 1.0) : 0.0;

    if (vKind == 2.0)
    {
        float lead = min(0.6, max(aTiming.z - aTiming.y, 0.0001));
        float landing = clamp(1.0 + (uTime - aTiming.z) / lead, 0.0, 1.0);
        position += aCueOffset * mix(1.2, 0.55, landing * landing);
        vColor.a *= uPlaying ? landing * (1.0 - smoothstep(aTiming.z, aTiming.z + 0.25, uTime)) : 0.0;
    }
    else if (uPlaying)
    {
        vColor.a *= 1.0 - smoothstep(aTiming.z + 0.1, aTiming.z + 0.45, uTime);
    }
    gl_Position = uViewProjection * vec4(position, 0.0, 1.0);
}
