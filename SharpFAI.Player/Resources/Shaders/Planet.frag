#version 330 core

in vec2 vUv;
in vec4 vTint;

uniform sampler2D uPlanetTex;

out vec4 FragColor;

void main()
{
    vec4 color = texture(uPlanetTex, vUv) * vTint;

    if (color.a <= 0.0001)
    {
        discard;
    }

    FragColor = color;
}
