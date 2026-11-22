#version 330 core

layout(location = 0) in vec2 aPosition;

uniform mat4 uViewProjection;
uniform vec2 uCenter;
uniform float uRadius;

void main()
{
    vec2 world = uCenter + aPosition * uRadius;
    gl_Position = uViewProjection * vec4(world, 0.0, 1.0);
}
