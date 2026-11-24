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
uniform bool uShowTiles;
uniform int uShapeMode;
uniform float uZoom;
uniform vec2 uShapeOffset;
uniform float uAngle;
uniform bool uHasImage;
uniform bool uRepeat;

vec4 over(vec4 bottom, vec3 rgb, float alpha)
{
    float total = alpha + bottom.a * (1.0 - alpha);
    return vec4(mix(bottom.rgb, rgb, alpha / max(total, 0.0001)), total);
}

float band(vec2 p, float tilt, float width, float length, float aa)
{
    float distance = max(abs(p.y - tilt * p.x) - width, abs(p.x) - length);
    return 1.0 - smoothstep(-aa, aa, distance);
}

vec4 defaultBackground(vec2 world)
{
    float aa = 1.0 / uZoom;
    vec4 result = vec4(0.0);
    if (uShowTiles)
    {
        // The tile layer follows the camera's position while retaining world-space size and orientation.
        vec2 cell = mat2(0.8734, -0.4872, 0.4872, 0.8734) * world / 90.0;
        vec2 corner = abs(fract(cell + 0.5) - 0.5) - 0.42;
        float distance = length(max(corner, 0.0)) + min(max(corner.x, corner.y), 0.0) - 0.07;
        float coverage = 1.0 - smoothstep(-aa / 90.0, aa / 90.0, distance);
        coverage = mix(0.96, coverage, smoothstep(1.0, 4.0, 90.0 * uZoom));
        result = vec4(uPattern.rgb, uPattern.a * coverage);
    }

    if (uShapeMode != 2)
    {
        vec2 p = world + uShapeOffset;
        vec2 triangle = p - vec2(-265.0, -165.0);
        float edge = max(-triangle.y - 86.0, abs(triangle.x) + 0.65 * triangle.y - 53.0);
        float triangleMask = 1.0 - smoothstep(-aa, aa, edge);
        float purpleBand = band(p - vec2(375.0, 135.0), 0.32, 17.0, 510.0, aa);
        float tealBand = band(p - vec2(510.0, -135.0), -0.35, 31.0, 390.0, aa);
        bool singleColor = uShapeMode == 1;
        result = over(result, singleColor ? uAccent.rgb : vec3(0.19, 0.13, 0.24), triangleMask * 0.42);
        result = over(result, singleColor ? uAccent.rgb : vec3(0.22, 0.14, 0.27), purpleBand * 0.42);
        result = over(result, singleColor ? uAccent.rgb : vec3(0.08, 0.21, 0.23), tealBand * 0.46);
    }
    return result;
}

void main()
{
    float c = cos(uAngle);
    float s = sin(uAngle);
    if (!uHasImage)
    {
        color = defaultBackground(mat2(c, s, -s, c) * (vScreen * uViewport) / uZoom);
        return;
    }

    vec2 pixels = mat2(c, -s, s, c) * (vScreen * uViewport) + uMovement;
    vec2 uv = pixels / uSize + 0.5;
    if (!uRepeat && (any(lessThan(uv, vec2(0.0))) || any(greaterThan(uv, vec2(1.0)))))
        discard;
    color = texture(uImage, uv) * uTint;
}
