#version 330 core

uniform sampler2D uFrame;
out vec4 packedBytes;

const vec3 luma = vec3(0.299, 0.587, 0.114);
const vec3 blueChroma = vec3(-0.168736, -0.331264, 0.5);
const vec3 redChroma = vec3(0.5, -0.418688, -0.081312);

vec3 rgb(int x, int y)
{
    return texelFetch(uFrame, ivec2(x, y), 0).rgb;
}

vec3 averageBlock(int x, int y)
{
    return (rgb(x, y) + rgb(x + 1, y) + rgb(x, y + 1) + rgb(x + 1, y + 1)) * 0.25;
}

void main()
{
    int height = textureSize(uFrame, 0).y;
    int row = int(gl_FragCoord.y);
    int x = int(gl_FragCoord.x) * 4;
    // The first byte of the raw stream is the top-left luma sample.
    if (row < height)
    {
        int y = height - 1 - row;
        packedBytes = (16.0 + 219.0 * vec4(dot(rgb(x, y), luma), dot(rgb(x + 1, y), luma),
            dot(rgb(x + 2, y), luma), dot(rgb(x + 3, y), luma))) / 255.0;
    }
    else
    {
        int y = height - 2 - (row - height) * 2;
        vec3 first = averageBlock(x, y);
        vec3 second = averageBlock(x + 2, y);
        packedBytes = (128.0 + 224.0 * vec4(dot(first, blueChroma), dot(first, redChroma),
            dot(second, blueChroma), dot(second, redChroma))) / 255.0;
    }
    // The readback requests the driver's native BGRA order.
    packedBytes = packedBytes.bgra;
}
