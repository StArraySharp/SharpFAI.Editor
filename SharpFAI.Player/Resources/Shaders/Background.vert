#version 330 core

out vec2 vScreen;

void main()
{
    vec2 uv = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2);
    vScreen = uv - 0.5;
    gl_Position = vec4(uv * 2.0 - 1.0, 0.0, 1.0);
}
