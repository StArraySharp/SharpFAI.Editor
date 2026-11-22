#version 330 core

in vec2 vScreen;
out vec4 color;
uniform sampler2D uImage;
uniform vec2 uViewport;
uniform vec2 uSize;
uniform vec2 uMovement;
uniform vec4 uTint;
uniform vec4 uPattern;
uniform vec4 uAccent;
uniform float uAngle;
uniform bool uHasImage;
uniform bool uRepeat;

void main()
{
    float c = cos(uAngle);
    float s = sin(uAngle);
    vec2 pixels = mat2(c, -s, s, c) * (vScreen * uViewport) + uMovement;
    vec2 uv = pixels / uSize + 0.5;
    if (uHasImage)
    {
        if (!uRepeat && (any(lessThan(uv, vec2(0.0))) || any(greaterThan(uv, vec2(1.0)))))
            discard;
        color = texture(uImage, uv) * uTint;
    }
    else
    {
        vec2 dist = abs(fract(uv) - 0.5);
        float border = smoothstep(0.475, 0.495, max(dist.x, dist.y));
        float accent = (1.0 - smoothstep(0.035, 0.065, dist.x + dist.y)) * uAccent.a * 0.12;
        color = vec4(mix(uPattern.rgb * (1.0 - border * 0.3), uAccent.rgb, accent), uPattern.a);
    }
}
