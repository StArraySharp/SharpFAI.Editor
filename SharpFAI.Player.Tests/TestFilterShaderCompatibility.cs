using SharpFAI.Player.Engine.Filters;
using SharpFAI.Player.Engine;
using SharpFAI.Player.Engine.ShaderGen;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;

namespace SharpFAI.Player.Tests;

public sealed class TestFilterShaderCompatibility
{
    private const string Target = "CameraFilterPack_Oculus_ThermaVision";
    private const string Fragment = """
        #version 150
        uniform float _Value;
        layout(location = 0) out vec4 exportedColor;
        void helper() { }
        void main()
        {
            float ratio = 0.5 / _Value;
            exportedColor = vec4(ratio) * vec4(0.0, -1.0, 0.0, 0.0) + vec4(1.0);
        }
        """;

    [TestCase(Target)]
    [TestCase("CameraFilterPack/Oculus_ThermaVision")]
    [TestCase("camerafilterpack_oculus_thermavision")]
    public void GuardsAtMainEntryAndPreservesOriginalSource(string name)
    {
        string source = Fragment.ReplaceLineEndings("\n");
        string result = FilterShaderCompatibility.AdaptFragment(name, source);
        int entry = source.IndexOf("void main()", StringComparison.Ordinal);
        int insertion = source.IndexOf('{', entry) + 1;
        const string guard = "\n    if (_Value == 0.0) { exportedColor = vec4(0.0); return; }\n";
        Assert.That(result, Is.EqualTo(source.Insert(insertion, guard)));
    }

    [TestCase("SV_Target0")]
    [TestCase("renamedOutput")]
    public void DiscoversOutputRatherThanTemporaryNames(string output)
    {
        string source = Fragment.Replace("exportedColor", output, StringComparison.Ordinal);
        string result = FilterShaderCompatibility.AdaptFragment(Target, source);
        Assert.That(result, Does.Contain($"if (_Value == 0.0) {{ {output} = vec4(0.0); return; }}"));
    }

    [Test]
    public void IgnoresCommentAndMacroDecoysAndPreservesCrLf()
    {
        string source = """
            // uniform float _Value; out vec4 decoy; void main() {
            /* void main() { } out vec4 other; */
            #define DECOY void main() { \
                out vec4 macroOutput;
            """ + "\n" + Fragment.Replace("void main()", "void /* gap */ main(void)", StringComparison.Ordinal);
        source = source.ReplaceLineEndings("\r\n");
        string result = FilterShaderCompatibility.AdaptFragment(Target, source);
        Assert.That(result, Does.Contain("{\r\n    if (_Value == 0.0) { exportedColor = vec4(0.0); return; }\r\n"));
        Assert.That(result.Replace("\r\n", "", StringComparison.Ordinal), Does.Not.Contain("\n"));
    }

    [TestCase("CameraFilterPack_Alien_Vision")]
    [TestCase("CameraFilterPack_Oculus_ThermaVision2")]
    [TestCase("Other/CameraFilterPack_Oculus_ThermaVision")]
    [TestCase("")]
    public void OtherShadersAreReturnedUnchangedEvenIfMalformed(string name)
    {
        const string malformed = "/* unterminated void main() {";
        Assert.That(FilterShaderCompatibility.AdaptFragment(name, malformed), Is.SameAs(malformed));
        Assert.That(FilterShaderCompatibility.AdaptFragment(name, Fragment), Is.SameAs(Fragment));
    }

    [TestCase("uniform float _Value;", "")]
    [TestCase("uniform float _Value;", "// uniform float _Value;")]
    [TestCase("uniform float _Value;", "uniform vec4 _Value;")]
    [TestCase("uniform float _Value;", "float _Value;")]
    [TestCase("out vec4 exportedColor;", "out vec3 exportedColor;")]
    [TestCase("out vec4 exportedColor;", "out vec4 exportedColor[2];")]
    [TestCase("layout(location = 0) out vec4 exportedColor;", "")]
    [TestCase("void main()", "void notMain()")]
    [TestCase("void main()", "void main(); void notMain()")]
    public void MissingRequiredDeclarationsFailClosed(string before, string after)
    {
        Assert.Throws<InvalidDataException>(() =>
            FilterShaderCompatibility.AdaptFragment(Target, Fragment.Replace(before, after, StringComparison.Ordinal)));
    }

    [TestCase("uniform float _Value;\n")]
    [TestCase("out vec4 anotherOutput;\n")]
    [TestCase("void main() { }\n")]
    [TestCase("/* unterminated\n")]
    [TestCase("{\n")]
    [TestCase("}\n")]
    public void AmbiguousOrTruncatedExportsFailClosed(string prefix)
    {
        Assert.Throws<InvalidDataException>(() => FilterShaderCompatibility.AdaptFragment(Target, prefix + Fragment));
    }

    [Test]
    public void LocalDeclarationsCannotStandInForRequiredGlobals()
    {
        const string source = "void main() { uniform float _Value; out vec4 localColor; }";
        Assert.Throws<InvalidDataException>(() => FilterShaderCompatibility.AdaptFragment(Target, source));
    }

    private const string Vhs = "CameraFilterPack/TV_VHS";
    private const string VhsCoordinates = """
        void main() {
            u_xlat1.x = u_xlat2.y;
            u_xlat5 = _TimeX * _Value3 + u_xlat2.y;
            u_xlat10.xy = (-u_xlat3.yy) * vec2(30.0, 5.0) + vec2(11.5, 1.5);
        }
        """;

    [TestCase(Vhs)]
    [TestCase("CameraFilterPack_TV_VHS")]
    [TestCase("camerafilterpack/tv_vhs")]
    public void VhsChangesOnlyTheThreeCoordinateExpressions(string name)
    {
        string source = "// u_xlat1.x = u_xlat2.y;\n" + VhsCoordinates;
        string expected = source.Replace("    u_xlat1.x = u_xlat2.y;",
                "    u_xlat2.y = 1.0 - u_xlat2.y; u_xlat1.x = u_xlat2.y;", StringComparison.Ordinal)
            .Replace("_TimeX * _Value3 + u_xlat2.y", "_TimeX * _Value3 + (1.0 - u_xlat2.y)", StringComparison.Ordinal)
            .Replace("(-u_xlat3.yy)", "(u_xlat3.yy - vec2(1.0))", StringComparison.Ordinal);
        Assert.That(FilterShaderCompatibility.AdaptFragment(name, source), Is.EqualTo(expected));
    }

    [TestCase("CameraFilterPack/TV_VHS_Rewind")]
    [TestCase("CameraFilterPackLegacy/TV_VHS")]
    [TestCase("CameraFilterPack/FX_Glitch3")]
    [TestCase("CameraFilterPack/TV_Distorted")]
    public void VhsCompatibilityDoesNotAffectOtherShaders(string name)
    {
        Assert.That(FilterShaderCompatibility.AdaptFragment(name, VhsCoordinates), Is.SameAs(VhsCoordinates));
    }

    [TestCase("u_xlat1.x = u_xlat2.y;", "")]
    [TestCase("u_xlat1.x = u_xlat2.y;", "/* u_xlat1.x = u_xlat2.y; */")]
    [TestCase("u_xlat1.x = u_xlat2.y;", "u_xlat1.x = u_xlat2.y; u_xlat1.x = u_xlat2.y;")]
    [TestCase("u_xlat5 = _TimeX * _Value3 + u_xlat2.y;", "u_xlat5 = _TimeX * _Value3 - u_xlat2.y;")]
    [TestCase("void main()", "void helper()")]
    [TestCase("}", "")]
    public void VhsUnexpectedExportsFailClosed(string before, string after)
    {
        Assert.Throws<InvalidDataException>(() => FilterShaderCompatibility.AdaptFragment(Vhs,
            VhsCoordinates.Replace(before, after, StringComparison.Ordinal)));
    }

    [Test]
    public void VhsRejectsExpressionsOutsideMainOrInTheWrongOrder()
    {
        const string assignment = "u_xlat1.x = u_xlat2.y;";
        string outside = "void helper() { " + assignment + " }\n" +
            VhsCoordinates.Replace(assignment, "", StringComparison.Ordinal);
        string reordered = VhsCoordinates.Replace(assignment, "", StringComparison.Ordinal)
            .Replace("}", assignment + " }", StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => FilterShaderCompatibility.AdaptFragment(Vhs, outside));
        Assert.Throws<InvalidDataException>(() => FilterShaderCompatibility.AdaptFragment(Vhs, reordered));
        Assert.Throws<InvalidDataException>(() => FilterShaderCompatibility.AdaptFragment(Vhs,
            FilterShaderCompatibility.AdaptFragment(Vhs, VhsCoordinates)));
    }

    [Test]
    public void VhsAcceptsWhitespaceAndPreservesUnchangedCrLf()
    {
        string source = VhsCoordinates.Replace(" = ", "\t=\t", StringComparison.Ordinal).ReplaceLineEndings("\r\n");
        string result = FilterShaderCompatibility.AdaptFragment(Vhs, source);
        Assert.That(result, Does.Contain("(1.0 - u_xlat2.y)"));
        Assert.That(result.Replace("\r\n", "", StringComparison.Ordinal), Does.Not.Contain("\n"));
    }

    [Test]
    [Explicit("Requires an OpenGL context and embedded game shader; compares D3D equations on the same GPU, not D3D execution.")]
    [NonParallelizable]
    [Apartment(ApartmentState.STA)]
    public void VhsFixedSourceAndTimeMatchesD3dCoordinateReference()
    {
        string export = ShaderLibrary.Load("Filters/CameraFilterPack/TV_VHS.glsl");
        string original = UnityShaderAdapter.ImportFullscreenFragment(export);
        string adapted = FilterShaderCompatibility.AdaptFragment(Vhs, original,
            export.StartsWith(FilterShaderCompatibility.HlslccDxbcHeader, StringComparison.Ordinal));
        const string reference = """
            #version 150
            uniform sampler2D _MainTex;
            uniform float _TimeX, _Value, _Value2, _Value3, _Value4;
            in vec2 vs_TEXCOORD0;
            out vec4 SV_Target0;
            float hash(vec2 p) { return fract(sin(dot(p, vec2(11.9898005,75.1330032))) * 43528.1484); }
            void main() {
                // D3D UV is top-down. The uploaded source texture is bottom-up.
                vec2 d = vec2(vs_TEXCOORD0.x, 1.0-vs_TEXCOORD0.y);
                float x = d.x + (hash(vec2(_TimeX,d.y))-0.5)/_Value;
                float roll = (1.0-d.y) + _TimeX*_Value3;
                roll = roll-floor(roll);
                roll += (hash(vec2(_TimeX,_TimeX))-0.5)/_Value2;
                float sampleY = 1.0-roll;
                vec4 color = texture(_MainTex,vec2(x,1.0-sampleY));
                color += (vec4(hash(vec2(d.y,_TimeX)),hash(vec2(d.y,_TimeX+1.0)),
                              hash(vec2(d.y,_TimeX+2.0)),0.0)-0.5)*0.100000001;
                float p = hash(vec2(floor(roll*80.0),floor(x*50.0))+vec2(_TimeX,0));
                if (!(p > 11.5-30.0*sampleY || p < 1.5-5.0*sampleY)) color += _Value4;
                SV_Target0 = color;
            }
            """;
        bool checkMainThread = GLFWProvider.CheckForMainThread;
        GLFWProvider.CheckForMainThread = false;
        try
        {
            using var window = new NativeWindow(new NativeWindowSettings
            {
                ClientSize = new Vector2i(64, 64), StartVisible = false, StartFocused = false,
                APIVersion = new Version(3, 3), Profile = ContextProfile.Core,
            });
            window.MakeCurrent();
            TestContext.Out.WriteLine(GL.GetString(StringName.Renderer));
            int vao = GL.GenVertexArray(), texture = GL.GenTexture();
            try
            {
                GL.BindVertexArray(vao);
                GL.ActiveTexture(TextureUnit.Texture0);
                GL.BindTexture(TextureTarget.Texture2D, texture);
                byte[] source = new byte[64 * 64 * 4];
                for (int y = 0; y < 64; y++)
                for (int x = 0; x < 64; x++)
                {
                    int i = (y * 64 + x) * 4;
                    source[i] = (byte)(x * 3); source[i + 1] = (byte)(y * 3);
                    source[i + 2] = (byte)((x ^ y) * 3); source[i + 3] = 255;
                }
                GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, 64, 64, 0,
                    PixelFormat.Rgba, PixelType.UnsignedByte, source);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
                GL.Viewport(0, 0, 64, 64);
                GL.Disable(EnableCap.Dither);
                foreach (float time in new[] { 1.125f, 1.75f, 2.375f })
                foreach (float value in new[] { 1f, -1f })
                {
                    byte[] expected = RenderVhs(reference, time, value);
                    byte[] actual = RenderVhs(adapted, time, value);
                    byte[] old = RenderVhs(original, time, value);
                    double Error(byte[] pixels) => pixels.Zip(expected, (a, b) => Math.Abs(a - b)).Average();
                    double error = Error(actual), oldError = Error(old);
                    TestContext.Out.WriteLine($"TimeX={time}, parameters={value}: adapted MAE={error:F6}, original MAE={oldError:F6}");
                    Assert.That(error, Is.LessThanOrEqualTo(1.0), "Same-source D3D coordinate equations must agree within RGBA8 precision.");
                    Assert.That(oldError, Is.GreaterThan(5.0), "Negative control must expose the platform mismatch.");
                }
                Assert.That(GL.GetError(), Is.EqualTo(ErrorCode.NoError));
            }
            finally { GL.DeleteTexture(texture); GL.DeleteVertexArray(vao); }
        }
        finally { GLFWProvider.CheckForMainThread = checkMainThread; }
    }

    private static byte[] RenderVhs(string fragment, float time, float value)
    {
        const string vertex = """
            #version 150
            out vec2 vs_TEXCOORD0;
            void main() {
                vec2 p = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2);
                vs_TEXCOORD0 = p;
                gl_Position = vec4(p*2.0-1.0,0,1);
            }
            """;
        int vs = GL.CreateShader(ShaderType.VertexShader), fs = GL.CreateShader(ShaderType.FragmentShader);
        int program = GL.CreateProgram();
        try
        {
            GL.ShaderSource(vs, vertex); GL.CompileShader(vs);
            GL.ShaderSource(fs, fragment); GL.CompileShader(fs);
            GL.GetShader(vs, ShaderParameter.CompileStatus, out int vsOk);
            GL.GetShader(fs, ShaderParameter.CompileStatus, out int fsOk);
            Assert.That(vsOk, Is.EqualTo(1), GL.GetShaderInfoLog(vs));
            Assert.That(fsOk, Is.EqualTo(1), GL.GetShaderInfoLog(fs));
            GL.AttachShader(program, vs); GL.AttachShader(program, fs); GL.LinkProgram(program);
            GL.GetProgram(program, GetProgramParameterName.LinkStatus, out int linked);
            Assert.That(linked, Is.EqualTo(1), GL.GetProgramInfoLog(program));
            GL.UseProgram(program);
            GL.Uniform1(GL.GetUniformLocation(program, "_MainTex"), 0);
            GL.Uniform4(GL.GetUniformLocation(program, "_MainTex_ST"), 1f, 1f, 0f, 0f);
            GL.Uniform1(GL.GetUniformLocation(program, "_TimeX"), time);
            foreach (string name in new[] { "_Value", "_Value2", "_Value3", "_Value4" })
                GL.Uniform1(GL.GetUniformLocation(program, name), value);
            GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
            byte[] pixels = new byte[64 * 64 * 4];
            GL.ReadPixels(0, 0, 64, 64, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
            return pixels;
        }
        finally { GL.UseProgram(0); GL.DeleteProgram(program); GL.DeleteShader(vs); GL.DeleteShader(fs); }
    }

    [Test]
    public void RequiredDeclarationsAfterMainFailClosed()
    {
        const string source = "void main() {} uniform float _Value; out vec4 color;";
        Assert.Throws<InvalidDataException>(() => FilterShaderCompatibility.AdaptFragment(Target, source));
    }
}
