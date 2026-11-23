using System.Security.Cryptography;
using System.Text.RegularExpressions;
using OpenTK.Graphics.OpenGL4;

namespace SharpFAI.Player.Tests;

public sealed partial class TestLegacyVhsCompatibility
{
    [Test, Explicit("Requires the Legacy004 D3D11/GL fixture and hlslcc 1.0 (SHARPFAI_HLSLCC).")]
    [NonParallelizable, Apartment(ApartmentState.STA)]
    public void HlslccMatchesOriginalLegacy004OnGpu()
    {
        string? exe = Environment.GetEnvironmentVariable("SHARPFAI_HLSLCC");
        Assert.That(exe, Is.Not.Null.And.Not.Empty, "Set SHARPFAI_HLSLCC to the absolute path of hlslcc.exe.");
        Assert.That(Path.IsPathFullyQualified(exe!) && File.Exists(exe), Is.True);
        RunControlledComparison(exe);
    }

    private sealed class HlslccExperiment : IDisposable
    {
        private readonly int[] buffers = new int[2];
        private readonly int[] samplers = new int[3];
        public int LinkedProgram { get; private set; }
        public object? Results { get; private set; }

        public void Initialize(string exe, string root, string dxbc, string asm)
        {
            // Confirm the register uses in the disassembly of the SHA-pinned fixture.
            Assert.That(Regex.Matches(asm, @"\bcb\d+\[\d+\]\.[xyzw]+", RegexOptions.IgnoreCase)
                .Select(m => Regex.Replace(m.Value.ToLowerInvariant(), @"\.x+$", ".x")).Distinct(),
                Is.EquivalentTo(new[] { "cb0[2].x", "cb0[2].y", "cb1[0].x" }));
            Assert.That(Regex.Matches(asm, @"(?m)^sample\b[^\r\n]*, (t\d+)\.xyzw, (s\d+)\s*$")
                .Select(m => $"{m.Groups[1].Value}/{m.Groups[2].Value}"),
                Is.EquivalentTo(new[] { "t0/s0", "t0/s0", "t1/s1", "t2/s2" }));

            string version = Run(exe, "--version", root).Trim();
            string executableHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(exe)));
            var variants = new List<object>();
            string instanced = "";
            foreach (var (name, flags) in new[]
            {
                ("default", ""), ("strip-unused", "--strip-unused"), ("no-default", "--no-default-flags"),
                ("instance", "--flag ubo-with-instance-name"),
                ("explicit-ubo-instance", "--no-default-flags --flag ubo --flag ubo-with-instance-name"),
            })
            {
                string path = Path.Combine(Path.GetDirectoryName(dxbc)!, name + ".glsl");
                string args = $"-l glsl330 {flags} -o \"{Path.GetRelativePath(root, path)}\" \"{Path.GetRelativePath(root, dxbc)}\"";
                try
                {
                    string cli = Run(exe, args, root).Trim();
                    string fragment = File.ReadAllText(path);
                    string stage = "";
                    string? error = null;
                    try
                    {
                        int program = Program(fragment, current => stage = current);
                        if (name == "instance") { instanced = fragment; LinkedProgram = program; }
                        else GL.DeleteProgram(program);
                    }
                    catch (InvalidOperationException ex) { error = ex.Message.Trim(); }
                    string status = error is null ? "fragment compiled and linked" : $"{stage} failed";
                    variants.Add(new { name, args, cli, status, error });
                    TestContext.Out.WriteLine($"hlslcc {name}: {status}\n{error}");
                }
                finally { File.Delete(path); }
            }

            Assert.That(LinkedProgram, Is.Not.Zero, "The raw instance-name variant must compile and link.");
            // Validate the emitted layout before binding it. No shader source is rewritten.
            for (int slot = 0; slot < 2; slot++)
            {
                int vec4Count = slot == 0 ? 4 : 2;
                string declaration = $@"layout\(std140\) uniform _cb{slot}\s*\{{\s*vec4 _m0\[{vec4Count}\];\s*\}}\s*__cb{slot};";
                Assert.That(Regex.Matches(instanced, declaration), Has.Count.EqualTo(1));
            }
            Assert.That(Regex.Matches(instanced, @"\b(__cb\d+)\._m0\[(\d+)\]\.([xyzw]+)")
                .Select(m => $"{m.Groups[1].Value}[{m.Groups[2].Value}].{m.Groups[3].Value}"),
                Is.EquivalentTo(new[] { "__cb0[2].x", "__cb0[2].y", "__cb1[0].xxxx", "__cb1[0].x", "__cb1[0].xxx", "__cb1[0].xx" }));
            Assert.That(Regex.Matches(instanced, @"\btexture\((_tex\d+),").Select(m => m.Groups[1].Value),
                Is.EquivalentTo(new[] { "_tex0", "_tex0", "_tex1", "_tex2" }));

            for (int slot = 0; slot < buffers.Length; slot++)
            {
                int block = GL.GetUniformBlockIndex(LinkedProgram, $"_cb{slot}");
                Assert.That(block, Is.GreaterThanOrEqualTo(0));
                GL.GetActiveUniformBlock(LinkedProgram, block, ActiveUniformBlockParameter.UniformBlockDataSize, out int bytes);
                Assert.That(bytes, Is.EqualTo(slot == 0 ? 64 : 32));
                GL.UniformBlockBinding(LinkedProgram, block, slot);
                buffers[slot] = GL.GenBuffer();
            }
            for (int slot = 0; slot < samplers.Length; slot++)
            {
                samplers[slot] = GL.GetUniformLocation(LinkedProgram, $"_tex{slot}");
                Assert.That(samplers[slot], Is.GreaterThanOrEqualTo(0));
            }
            Results = new { exe, version, executableHash, variants, renderedVariant = "instance", bindingStatus = "both UBOs and all three samplers active" };
        }

        public void Bind(Case c, int misboundTexture)
        {
            // b0.c2.xy = TRACKING, CONTRAST. b1.c0 = Unity _Time (only x read).
            Upload(0, new float[16] { 0, 0, 0, 0, 0, 0, 0, 0, c.Tracking, 0, 0, 0, 0, 0, 0, 0 });
            Upload(1, new float[8] { c.Seconds / 20, c.Seconds, c.Seconds * 2, c.Seconds * 3, 0, 0, 0, 0 });
            // t0/s0 = source RT/clamp, t1/s1 = VHS/repeat, t2/s2 = VHS2/repeat.
            for (int slot = 0; slot < samplers.Length; slot++)
                GL.Uniform1(samplers[slot], slot == misboundTexture ? (slot + 1) % 3 : slot);
        }

        private void Upload(int slot, float[] data)
        {
            GL.BindBuffer(BufferTarget.UniformBuffer, buffers[slot]);
            GL.BufferData(BufferTarget.UniformBuffer, data.Length * sizeof(float), data, BufferUsageHint.StreamDraw);
            GL.BindBufferBase(BufferRangeTarget.UniformBuffer, slot, buffers[slot]);
        }

        public void Dispose()
        {
            if (LinkedProgram != 0) GL.DeleteProgram(LinkedProgram);
            foreach (int buffer in buffers)
                if (buffer != 0) GL.DeleteBuffer(buffer);
        }
    }
}
