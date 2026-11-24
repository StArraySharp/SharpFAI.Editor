#version 330 core

uniform vec2 uProjectionScale;
uniform vec2 uParallaxOffset;
uniform float uAngle;
uniform bool uSingleColor;
uniform vec3 uShapeColor;
flat out vec4 vTint;

// Transform and tint of the rectangular shapes in TutorialBackground1.
// Centre, rotation in degrees, followed by the dimensions of the unit sprite.
const vec3 centres[8] = vec3[8](
    vec3(7.4315, -4.212, 0.0), vec3(9.256, -3.666, 0.0),
    vec3(6.571, 1.153, 7.4898305), vec3(10.21, 2.58, 0.0),
    vec3(8.729, 3.007, 0.0), vec3(5.97, 2.1, -6.725595),
    vec3(7.45, -2.729, 4.1305275), vec3(6.7428, 2.475, 8.276069)
);
const vec2 sizes[8] = vec2[8](
    vec2(3.800425, 1.2447382), vec2(3.0018861, 1.0),
    vec2(3.3076246, 3.344893), vec2(1.8328216, 1.8233424),
    vec2(1.5438124, 1.5342225), vec2(12.28, 0.08),
    vec2(21.969847, 0.06851201), vec2(11.840065, 0.08130768)
);
const vec4 tones[8] = vec4[8](
    vec4(0.287, 0.16348101, 0.27246833, 0.559), vec4(0.013094117, 0.371, 0.27788627, 0.36),
    vec4(0.035940297, 0.049313426, 0.056, 0.597), vec4(0.18867922, 0.13685887, 0.0, 0.697),
    vec4(0.20573439, 0.12632813, 0.231, 0.409), vec4(0.12896968, 0.20363635, 0.224, 0.484),
    vec4(0.19844186, 0.19469766, 0.322, 0.759), vec4(0.301, 0.2037256, 0.27714023, 0.616)
);

const vec2 quad[6] = vec2[6](
    vec2(-0.5, -0.5), vec2(0.5, -0.5), vec2(0.5, 0.5),
    vec2(-0.5, -0.5), vec2(0.5, 0.5), vec2(-0.5, 0.5)
);
const vec2 triangle[3] = vec2[3](
    vec2(-3.0171635, -2.682907), vec2(-5.5446813, -4.6356789), vec2(-2.5700915, -5.8348316)
);

vec2 rotate(vec2 p, float angle)
{
    float c = cos(angle);
    float s = sin(angle);
    return mat2(c, s, -s, c) * p;
}

void main()
{
    int shape = gl_VertexID / 6;
    vec2 position;
    if (shape < 8)
    {
        vec3 transform = centres[shape];
        position = transform.xy + rotate(quad[gl_VertexID % 6] * sizes[shape], radians(transform.z));
        vTint = tones[shape];
    }
    else
    {
        position = triangle[gl_VertexID - 48];
        vTint = vec4(0.294, 0.22482353, 0.0, 0.391);
    }
    if (uSingleColor)
        vTint.rgb = uShapeColor;
    vec2 view = rotate(position - uParallaxOffset, -uAngle) * uProjectionScale;
    gl_Position = vec4(view, 0.0, 1.0);
}
