#version 330 core

in vec2 vScreen;
out vec4 color;
uniform sampler2D uImage;
uniform vec2 uViewport;
uniform vec2 uSize;
uniform vec2 uMovement;
uniform vec4 uTint;
uniform vec4 uPattern;
uniform float uAngle;
uniform bool uHasImage;
uniform bool uHasTutorialTile;
uniform bool uRepeat;

vec4 tutorialTile(vec2 screen)
{
    // BGStaticCam has a 10-unit view height and the sprite follows its position 1:1.
    float angle = uAngle - radians(29.156113);
    float c = cos(angle);
    float s = sin(angle);
    vec2 local = mat2(c, s, -s, c) * screen * (10.0 / 1.8714299);
    vec2 halfSize = vec2(13.126585, 12.5) * 0.5;
    if (any(greaterThan(abs(local), halfSize)))
        discard;

    vec2 uv = (local + halfSize) / 0.48;
    if (uHasTutorialTile)
        return texture(uImage, uv) * uPattern;

    // Source-only builds can still display a pattern before preparing game assets.
    vec2 square = abs(fract(uv + 0.5) - 0.5);
    return vec4(uPattern.rgb, uPattern.a * step(max(square.x, square.y), 0.15));
}

void main()
{
    float c = cos(uAngle);
    float s = sin(uAngle);
    if (!uHasImage)
    {
        color = tutorialTile(vScreen * vec2(uViewport.x / uViewport.y, 1.0));
        return;
    }

    vec2 pixels = mat2(c, -s, s, c) * (vScreen * uViewport) + uMovement;
    vec2 uv = pixels / uSize + 0.5;
    if (!uRepeat && (any(lessThan(uv, vec2(0.0))) || any(greaterThan(uv, vec2(1.0)))))
        discard;
    color = texture(uImage, uv) * uTint;
}
