using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AsmResolver.DotNet;
using AsmResolver.PE.DotNet.Cil;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.TypeSystem;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace SharpFAI.SourceGenerator;

[Generator]
public sealed class AdofaiFilterGenerator : IIncrementalGenerator
{
    private static readonly DiagnosticDescriptor InvalidInput = new(
        "SFAI001", "Invalid ADOFAI filter input", "{0}", "Filters", DiagnosticSeverity.Error, true);
    private static readonly DiagnosticDescriptor UnsupportedComponent = new(
        "SFAI002", "ADOFAI filter conversion skipped", "{0}: {1}", "Filters", DiagnosticSeverity.Warning, true);

    private static readonly HashSet<string> InitialComponents = new(StringComparer.Ordinal)
    {
        "CameraFilterPackLegacy_Color_Invert",
        "CameraFilterPackLegacy_Color_GrayScale",
        "CameraFilterPack_Color_Invert",
        "CameraFilterPack_Real_VHS",
    };

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var inputs = context.AdditionalTextsProvider
            .Where(file => Path.GetFileName(file.Path) == "AdofaiFilters.inputs")
            .Select((file, token) => file.GetText(token)?.ToString() ?? throw new IOException(file.Path))
            .Collect();

        context.RegisterSourceOutput(inputs.Combine(context.CompilationProvider), (output, input) =>
        {
            try
            {
                Generate(output, input.Left.IsEmpty ? "" : input.Left.Single(), input.Right);
            }
            catch (Exception error) when (!(error is OperationCanceledException))
            {
                output.ReportDiagnostic(Diagnostic.Create(InvalidInput, Location.None, error.Message));
                EmitRegistry(output, new(), new(), Array.Empty<string>());
            }
        });
    }

    private static void Generate(SourceProductionContext output, string manifest, Compilation compilation)
    {
        List<string> factories = new();
        List<string> legacy = new();
        SortedDictionary<string, string> coverage = new(StringComparer.Ordinal);
        List<Candidate> candidates = new();

        if (string.IsNullOrWhiteSpace(manifest))
        {
            EmitRegistry(output, factories, legacy, coverage.Values);
            return;
        }

        Dictionary<string, string> paths = new(StringComparer.Ordinal);
        HashSet<string> packaged = new(StringComparer.Ordinal);
        HashSet<string> multiExport = new(StringComparer.Ordinal);
        foreach (string line in manifest.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string[] fields = line.Split('|');
            if (fields.Length == 4 && fields[0] == "R")
            {
                packaged.Add(fields[1]);
                int export = fields[1].LastIndexOf(".export-", StringComparison.Ordinal);
                if (export >= 0 && fields[1].EndsWith(".glsl", StringComparison.Ordinal))
                    multiExport.Add(fields[1].Substring(0, export) + ".glsl");
                continue;
            }
            if (fields.Length != 3 || fields[0] != "A")
                throw new IOException("Malformed ADOFAI filter input: " + line);
            string path = fields[1];
            // MSBuild tracks the binary input by content hash in AdditionalFiles.
#pragma warning disable RS1035
            using var stream = File.OpenRead(path);
#pragma warning restore RS1035
            using var sha = SHA256.Create();
            string hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
            if (!string.Equals(hash, fields[2], StringComparison.OrdinalIgnoreCase))
                throw new IOException("ADOFAI filter input changed during compilation: " + path);
            paths.Add(Path.GetFileName(path), path);
        }

        string firstpass = paths["Assembly-CSharp-firstpass.dll"];
        string game = paths["Assembly-CSharp.dll"];
        var components = ModuleDefinition.FromFile(firstpass).GetAllTypes()
            .Where(type => type.Name?.ToString().StartsWith("CameraFilterPack", StringComparison.Ordinal) == true)
            .OrderBy(type => type.FullName, StringComparer.Ordinal);
        CSharpDecompiler decompiler = OpenDecompiler(firstpass);
        CSharpDecompiler gameDecompiler = OpenDecompiler(game);
        string mappings = gameDecompiler.DecompileTypeAsString(new FullTypeName("scrVfxPlus"));
        string intensity = gameDecompiler.DecompileTypeAsString(new FullTypeName("ffxSetFilterPlus"));

        foreach (var component in components)
        {
            output.CancellationToken.ThrowIfCancellationRequested();
            string name = component.FullName;
            string[] required = FindResources(component).Distinct(StringComparer.Ordinal).ToArray();
            string[] missing = required.Where(item => !packaged.Contains(
                (item.EndsWith(".glsl", StringComparison.Ordinal) ? "Resources.Shaders.Filters/" : "Resources.Filters.") + item)).ToArray();
            string resourceStatus = required.Length == 0 ? "unresolved" :
                missing.Length == 0 ? "packaged" : "missing " + string.Join(", ", missing);
            bool initial = InitialComponents.Contains(name);
            string[] shaders = required.Where(item => item.EndsWith(".glsl", StringComparison.Ordinal)).ToArray();
            if (!initial && (missing.Length != 0 || shaders.Length != 1 ||
                multiExport.Contains("Resources.Shaders.Filters/" + shaders[0])))
            {
                coverage[name] = Report(name, "pending conversion", resourceStatus,
                    missing.Length != 0 ? "required resource unavailable" : "shader selection or multi-export layout requires mapping");
                continue;
            }

            try
            {
                string source = decompiler.DecompileTypeAsString(new FullTypeName(name));
                ClassDeclarationSyntax declaration = SyntaxFactory.ParseCompilationUnit(source)
                    .DescendantNodes().OfType<ClassDeclarationSyntax>().Single();
                string generated = Convert(component, declaration);
                Match mapping = Regex.Match(mappings, @"filterToComp\.Add\(Filter\.(\w+), GetFilter<" + name + @">\(\)\)");
                string? binding = null;
                string detail = string.Join(", ", required);
                if (mapping.Success)
                {
                    try { binding = LegacyBinding(mapping.Groups[1].Value, name, mappings, intensity, declaration); }
                    catch (InvalidOperationException error) { detail += "; " + error.Message; }
                }
                var tree = CSharpSyntaxTree.ParseText(SourceText.From(generated, Encoding.UTF8),
                    (CSharpParseOptions?)compilation.SyntaxTrees.FirstOrDefault()?.Options, name + ".g.cs", output.CancellationToken);
                candidates.Add(new Candidate(name, tree, resourceStatus, binding, detail));
            }
            catch (Exception error) when (!(error is OperationCanceledException))
            {
                if (initial)
                    output.ReportDiagnostic(Diagnostic.Create(UnsupportedComponent, Location.None, name, error.Message));
                coverage[name] = Report(name, "conversion skipped", resourceStatus, error.Message);
            }
        }

        compilation = compilation.AddSyntaxTrees(candidates.Select(candidate => candidate.Tree));
        foreach (var candidate in candidates)
        {
            var error = compilation.GetSemanticModel(candidate.Tree).GetDiagnostics(cancellationToken: output.CancellationToken)
                .FirstOrDefault(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
            if (error is not null)
            {
                string message = error.GetMessage(CultureInfo.InvariantCulture);
                if (InitialComponents.Contains(candidate.Name))
                    output.ReportDiagnostic(Diagnostic.Create(UnsupportedComponent, Location.None, candidate.Name, message));
                coverage[candidate.Name] = Report(candidate.Name, "conversion skipped", candidate.ResourceStatus, message);
                continue;
            }
            output.AddSource(candidate.Name + ".g.cs", candidate.Tree.GetText(output.CancellationToken));
            factories.Add("[" + Quote(candidate.Name) + "] = static backend => new " + candidate.Name + "(backend),");
            if (candidate.Binding is { } binding)
                legacy.Add(binding);
            coverage[candidate.Name] = Report(candidate.Name, "generated", candidate.ResourceStatus, candidate.Detail);
        }
        EmitRegistry(output, factories, legacy, coverage.Values);
    }

    private sealed class Candidate(string name, SyntaxTree tree, string resourceStatus, string? binding, string detail)
    {
        public string Name { get; } = name;
        public SyntaxTree Tree { get; } = tree;
        public string ResourceStatus { get; } = resourceStatus;
        public string? Binding { get; } = binding;
        public string Detail { get; } = detail;
    }

    private static string Report(string name, string conversion, string resources, string detail) =>
        Quote(name + "\t" + conversion + "\t" + resources + "\tunverified\tunverified\t" +
            detail.Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' '));

    private static IEnumerable<string> FindResources(TypeDefinition type)
    {
        var strings = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var method in type.Methods)
        {
            if (method.CilMethodBody?.Instructions is not { } instructions)
                continue;
            for (int index = 1; index < instructions.Count; index++)
            {
                var instruction = instructions[index];
                if (instruction.OpCode.Code is not (CilCode.Stfld or CilCode.Stsfld) ||
                    instruction.Operand is not IFieldDescriptor field)
                    continue;
                string? literal = instructions[index - 1].Operand as string;
                if (strings.TryGetValue(field.FullName, out string? previous) && previous != literal)
                    literal = null;
                strings[field.FullName] = literal;
            }
        }

        foreach (var method in type.Methods)
        {
            if (method.CilMethodBody?.Instructions is not { } instructions)
                continue;
            for (int index = 1; index < instructions.Count; index++)
            {
                if (instructions[index].Operand is not IMethodDescriptor call)
                    continue;
                string signature = call.FullName;
                string? suffix = signature.Contains("UnityEngine.Shader::Find(System.String)") ? ".glsl" :
                    signature.Contains("UnityEngine.Resources::Load(System.String)") ? ".png" : null;
                if (suffix is null)
                    continue;
                var argument = instructions[index - 1];
                string? name = argument.Operand as string;
                if (argument.OpCode.Code is CilCode.Ldfld or CilCode.Ldsfld && argument.Operand is IFieldDescriptor field)
                    strings.TryGetValue(field.FullName, out name);
                if (!string.IsNullOrWhiteSpace(name))
                    yield return name + suffix;
            }
        }
    }

    private static CSharpDecompiler OpenDecompiler(string path) => new(path, new DecompilerSettings
    {
        ThrowOnAssemblyResolveErrors = true,
    });

    private static string LegacyBinding(string filter, string name, string mappings, string intensity,
        ClassDeclarationSyntax declaration)
    {
        Match defaultValue = Regex.Match(mappings, @"\{\s*Filter\." + filter + @",\s*(-?[\d.]+f?)\s*\}");
        string initial = defaultValue.Success ? defaultValue.Groups[1].Value : "0f";
        Match conversion = Regex.Match(intensity, @"case Filter\." + filter + @":(?<body>[\s\S]*?)(?=case Filter\.|default:|\z)");
        string binding = "null";
        if (conversion.Success)
        {
            string body = conversion.Groups["body"].Value;
            var matches = Regex.Matches(body, @"(?:(?<instance>as " + name + @"\))|" + name + @")\.(?<field>\w+)\s*=\s*(?<value>[^;]+);");
            List<string> assignments = new();
            foreach (Match match in matches)
            {
                string field = match.Groups["field"].Value;
                bool instance = match.Groups["instance"].Success;
                bool known = declaration.Members.OfType<FieldDeclarationSyntax>().Any(item =>
                    item.Modifiers.Any(SyntaxKind.PublicKeyword) &&
                    item.Modifiers.Any(SyntaxKind.StaticKeyword) != instance &&
                    item.Declaration.Type.ToString() is "float" or "int" &&
                    item.Declaration.Variables.Any(variable => variable.Identifier.ValueText == field));
                string? value = FormatIntensity(SyntaxFactory.ParseExpression(match.Groups["value"].Value));
                if (!known || value is null)
                    throw new InvalidOperationException("Unsupported legacy intensity assignment in ffxSetFilterPlus.SetFilter.");
                assignments.Add((instance ? "((" + name + ")filter)" : name) + "." + field + " = " + value + ";");
            }
            if (assignments.Count != 0)
                binding = "(static (filter, fIntensity) => { " + string.Join(" ", assignments) + " })";
            else if (body.Contains(name))
                throw new InvalidOperationException("Unsupported legacy intensity assignment in ffxSetFilterPlus.SetFilter.");
        }

        int order = mappings.IndexOf("filterToComp.Add(Filter." + filter + ",", StringComparison.Ordinal);
        return "[" + Quote(filter) + "] = new(" + Quote(name) + ", " + initial + ", " +
            order.ToString(CultureInfo.InvariantCulture) + ", " + binding + "),";
    }

    private static string? FormatIntensity(ExpressionSyntax expression)
    {
        switch (expression)
        {
            case IdentifierNameSyntax identifier when identifier.Identifier.ValueText == "fIntensity":
            case LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.NumericLiteralExpression):
                return expression.ToString();
            case ParenthesizedExpressionSyntax group when FormatIntensity(group.Expression) is { } value:
                return "(" + value + ")";
            case PrefixUnaryExpressionSyntax prefix when prefix.IsKind(SyntaxKind.UnaryMinusExpression) &&
                FormatIntensity(prefix.Operand) is { } value:
                return "-(" + value + ")";
            case BinaryExpressionSyntax binary when binary.Kind() is SyntaxKind.AddExpression or SyntaxKind.SubtractExpression
                or SyntaxKind.MultiplyExpression or SyntaxKind.DivideExpression &&
                FormatIntensity(binary.Left) is { } left && FormatIntensity(binary.Right) is { } right:
                return "(" + left + " " + binary.OperatorToken + " " + right + ")";
            case InvocationExpressionSyntax call:
                string?[] args = call.ArgumentList.Arguments.Select(arg => FormatIntensity(arg.Expression)).ToArray();
                if (args.Any(arg => arg is null))
                    return null;
                return (call.Expression.ToString(), args.Length) switch
                {
                    ("Mathf.Max", 2) => "Math.Max(" + string.Join(", ", args) + ")",
                    ("Mathf.Clamp", 3) => "Math.Clamp(" + string.Join(", ", args) + ")",
                    ("Mathf.RoundToInt", 1) => "(int)MathF.Round(" + args[0] + ")",
                    ("Mathf.Lerp", 3) => "(" + args[0] + " + (" + args[1] + " - " + args[0] +
                        ") * Math.Clamp(" + args[2] + ", 0f, 1f))",
                    _ => null,
                };
            default:
                return null;
        }
    }

    private static string Convert(TypeDefinition component, ClassDeclarationSyntax declaration)
    {
        string name = declaration.Identifier.ValueText;
        var lifecycle = new HashSet<string>(StringComparer.Ordinal) { "Awake", "Start", "OnEnable", "Update", "OnRenderImage", "OnDisable" };
        var rewriter = new UnityFilterRewriter();
        var methods = declaration.Members.OfType<MethodDeclarationSyntax>().ToArray();
        if (methods.Any(method => method.Identifier.ValueText is "LateUpdate" or "OnPreCull" or "OnPreRender" or "OnPostRender" or "OnDestroy"))
            throw new InvalidOperationException("Additional Unity lifecycle callback requires a rendering adapter.");
        if (declaration.Members.OfType<ConstructorDeclarationSyntax>().Any(constructor => constructor.Body?.Statements.Count > 0))
            throw new InvalidOperationException("Explicit constructor requires conversion.");
        var selected = declaration.Members.Where(member => !(member is MethodDeclarationSyntax method) ||
            lifecycle.Contains(method.Identifier.ValueText)).ToList();
        for (int index = 0; index < selected.Count; index++)
        {
            var calls = selected[index].DescendantNodes().OfType<InvocationExpressionSyntax>();
            foreach (var call in calls)
            {
                if (call.Expression is IdentifierNameSyntax identifier)
                    foreach (var helper in methods.Where(method => method.Identifier.ValueText == identifier.Identifier.ValueText))
                        if (!selected.Contains(helper))
                            selected.Add(helper);
            }
        }

        if (!methods.Any(method => method.Identifier.ValueText == "OnRenderImage"))
            throw new InvalidOperationException("OnRenderImage was not found.");

        ValidateCalls(component, selected);
        var fields = declaration.Members.OfType<FieldDeclarationSyntax>()
            .Where(field => field.Modifiers.Any(SyntaxKind.PublicKeyword) && !field.Modifiers.Any(SyntaxKind.StaticKeyword) &&
                field.Declaration.Type.ToString() is "float" or "int" or "Vector2" or "Color")
            .SelectMany(field => field.Declaration.Variables.Select(variable =>
                (Name: variable.Identifier.ValueText, Type: field.Declaration.Type.ToString()))).ToArray();
        string Has(string method) => methods.Any(item => item.Identifier.ValueText == method &&
            (item.Body?.Statements.Count != 0 || item.ExpressionBody is not null)) ? method + "();" : "";
        foreach (var member in selected)
            foreach (var variable in member.DescendantNodes().OfType<VariableDeclarationSyntax>()
                .Where(declaration => declaration.Type.ToString() == "int")
                .SelectMany(declaration => declaration.Variables))
                rewriter.Integers.Add(variable.Identifier.ValueText);
        string members = string.Join("\n", selected.Select(member =>
        {
            rewriter.Member = member is MethodDeclarationSyntax method ? method.Identifier.ValueText : member.Kind().ToString();
            return rewriter.Visit(member)!.NormalizeWhitespace().ToFullString();
        }));
        return $$"""
            // <auto-generated />
            #nullable disable
            using System;
            using System.Numerics;
            namespace SharpFAI.Player.Engine.Filters;
            internal sealed class {{name}} : IGeneratedFilter
            {
                private readonly IFilterBackend backend;
                private static float Clamp01(float value) => Math.Clamp(value, 0f, 1f);
                private bool started;
                private readonly bool initializeLifecycle;
                internal {{name}}(IFilterBackend backend, bool cameraDefaults = false, bool initializeLifecycle = true)
                {
                    this.backend = backend;
                    this.initializeLifecycle = initializeLifecycle;
                    if (cameraDefaults)
                        CameraFilterDefaults.Apply(this, "{{name}}");
                    if (initializeLifecycle) { {{Has("Awake")}} }
                }
                public void Enable() { {{Has("OnEnable")}} }
                public void Render(IFilterTarget source, IFilterTarget destination)
                {
                    if (!started)
                    {
                        {{Has("Start")}}
                        started = true;
                    }
                    {{Has("Update")}}
                    OnRenderImage(source, destination);
                }
                public void Dispose() { if (initializeLifecycle) { {{Has("OnDisable")}} } }
                public System.Collections.Generic.IReadOnlyList<string> Parameters { get; } = [{{string.Join(", ", fields.Select(field => Quote(field.Name)))}}];
                public FilterParameterKind ParameterKind(string field) => field switch
                {
                    {{string.Join("\n", fields.Where(field => field.Type != "float")
                        .Select(field => Quote(field.Name) + " => FilterParameterKind." + (field.Type == "int" ? "Integer" : field.Type) + ","))}}
                    _ => FilterParameterKind.Float,
                };
                public Vector4? GetParameter(string field) => field switch
                {
                    {{string.Join("\n", fields.Select(field => Quote(field.Name) + " => " + PackParameter(field.Name, field.Type) + ","))}}
                    _ => null,
                };
                public void SetParameter(string field, Vector4 value)
                {
                    switch (field)
                    {
                        {{string.Join("\n", fields.Select(field => "case " + Quote(field.Name) + ": " + field.Name +
                            " = " + UnpackParameter(field.Type) + "; break;"))}}
                        default: break;
                    }
                }
                {{members}}
            }
            """;
    }

    private static string PackParameter(string name, string type) => type switch
    {
        "Color" => name,
        "Vector2" => "new Vector4(" + name + ", 0f, 0f)",
        _ => "new Vector4(" + name + ", 0f, 0f, 0f)",
    };

    private static string UnpackParameter(string type) => type switch
    {
        "Color" => "value",
        "Vector2" => "new Vector2(value.X, value.Y)",
        "int" => "(int)Math.Clamp(Math.Round((double)value.X), int.MinValue, int.MaxValue)",
        _ => "value.X",
    };

    private static void ValidateCalls(TypeDefinition component, IEnumerable<MemberDeclarationSyntax> selected)
    {
        var names = new HashSet<string>(StringComparer.Ordinal) { ".ctor", ".cctor" };
        foreach (var member in selected)
        {
            if (member is MethodDeclarationSyntax method)
                names.Add(method.Identifier.ValueText);
            if (member is PropertyDeclarationSyntax property)
            {
                names.Add("get_" + property.Identifier.ValueText);
                names.Add("set_" + property.Identifier.ValueText);
            }
        }

        foreach (var method in component.Methods.Where(method => names.Contains(method.Name?.ToString() ?? "")))
        {
            if (FindUnsupportedCall(method) is { } unsupported)
                throw new InvalidOperationException(unsupported);
        }
    }

    private static string? FindUnsupportedCall(MethodDefinition method)
    {
        if (method.CilMethodBody?.Instructions is not { } instructions)
            return method.Name + ": method body missing.";
        foreach (var instruction in instructions)
        {
            if (instruction.Operand is not IMethodDescriptor call ||
                call.DeclaringType?.FullName is not { } owner ||
                !owner.StartsWith("UnityEngine.", StringComparison.Ordinal))
                continue;
            string signature = call.FullName;
            int start = signature.IndexOf(owner + "::", StringComparison.Ordinal);
            if (start < 0 || !SupportedCalls.Contains(signature.Substring(start)))
                return $"{method.Name} at IL_{instruction.Offset:x4}: {signature}";
        }
        return null;
    }

    private static readonly HashSet<string> SupportedCalls = new(StringComparer.Ordinal)
    {
        "UnityEngine.MonoBehaviour::.ctor()",
        "UnityEngine.Object::op_Equality(UnityEngine.Object, UnityEngine.Object)",
        "UnityEngine.Object::op_Inequality(UnityEngine.Object, UnityEngine.Object)",
        "UnityEngine.Object::op_Implicit(UnityEngine.Object)",
        "UnityEngine.Object::set_hideFlags(UnityEngine.HideFlags)",
        "UnityEngine.Object::DestroyImmediate(UnityEngine.Object)",
        "UnityEngine.Shader::Find(System.String)",
        "UnityEngine.Material::.ctor(UnityEngine.Shader)",
        "UnityEngine.Material::SetFloat(System.String, System.Single)",
        "UnityEngine.Material::SetInt(System.String, System.Int32)",
        "UnityEngine.Material::SetColor(System.String, UnityEngine.Color)",
        "UnityEngine.Material::SetVector(System.String, UnityEngine.Vector4)",
        "UnityEngine.Material::SetTexture(System.String, UnityEngine.Texture)",
        "UnityEngine.Vector4::.ctor(System.Single, System.Single, System.Single, System.Single)",
        "UnityEngine.Vector2::.ctor(System.Single, System.Single)",
        "UnityEngine.Vector2::get_zero()",
        "UnityEngine.Vector2::get_one()",
        "UnityEngine.Vector2::x",
        "UnityEngine.Vector2::y",
        "UnityEngine.Vector4::x",
        "UnityEngine.Vector4::y",
        "UnityEngine.Vector4::z",
        "UnityEngine.Vector4::w",
        "UnityEngine.Vector4::op_Implicit(UnityEngine.Vector2)",
        "UnityEngine.Color::.ctor(System.Single, System.Single, System.Single)",
        "UnityEngine.Color::.ctor(System.Single, System.Single, System.Single, System.Single)",
        "UnityEngine.Color::r",
        "UnityEngine.Color::g",
        "UnityEngine.Color::b",
        "UnityEngine.Color::a",
        "UnityEngine.Color::get_white()",
        "UnityEngine.Color::get_black()",
        "UnityEngine.Color::get_clear()",
        "UnityEngine.Color::get_red()",
        "UnityEngine.Color::get_green()",
        "UnityEngine.Color::get_blue()",
        "UnityEngine.Color::op_Implicit(UnityEngine.Color)",
        "UnityEngine.Color::op_Implicit(UnityEngine.Vector4)",
        "UnityEngine.Application::get_isPlaying()",
        "UnityEngine.Time::get_deltaTime()",
        "UnityEngine.Screen::get_width()",
        "UnityEngine.Screen::get_height()",
        "UnityEngine.Mathf::Round(System.Single)",
        "UnityEngine.Mathf::Min(System.Single, System.Single)",
        "UnityEngine.Mathf::Max(System.Single, System.Single)",
        "UnityEngine.Mathf::Min(System.Int32, System.Int32)",
        "UnityEngine.Mathf::Max(System.Int32, System.Int32)",
        "UnityEngine.Mathf::Clamp(System.Single, System.Single, System.Single)",
        "UnityEngine.Mathf::Clamp(System.Int32, System.Int32, System.Int32)",
        "UnityEngine.Mathf::Clamp01(System.Single)",
        "UnityEngine.Resources::Load(System.String)",
        "UnityEngine.Texture::get_width()",
        "UnityEngine.Texture::get_height()",
        "UnityEngine.Texture::set_filterMode(UnityEngine.FilterMode)",
        "UnityEngine.RenderTexture::GetTemporary(System.Int32, System.Int32, System.Int32)",
        "UnityEngine.RenderTexture::ReleaseTemporary(UnityEngine.RenderTexture)",
        "UnityEngine.Graphics::Blit(UnityEngine.Texture, UnityEngine.RenderTexture)",
        "UnityEngine.Graphics::Blit(UnityEngine.Texture, UnityEngine.RenderTexture, UnityEngine.Material)",
        "UnityEngine.Graphics::Blit(UnityEngine.Texture, UnityEngine.RenderTexture, UnityEngine.Material, System.Int32)",
    };

    private static void EmitRegistry(SourceProductionContext output, List<string> factories, List<string> legacy, IEnumerable<string> coverage)
    {
        output.AddSource("GeneratedFilterRegistry.g.cs", SourceText.From($$"""
            // <auto-generated />
            using System;
            using System.Collections.Generic;
            namespace SharpFAI.Player.Engine.Filters;
            internal static class GeneratedFilterRegistry
            {
                internal static readonly Dictionary<string, Func<IFilterBackend, IGeneratedFilter>> Factories = new(StringComparer.Ordinal)
                {
                    {{string.Join("\n", factories)}}
                };
                internal static readonly Dictionary<string, Func<IFilterBackend, IGeneratedFilter>> CameraFactories = new(StringComparer.Ordinal)
                {
                    {{string.Join("\n", factories.Select(factory => factory.Replace("(backend),", "(backend, true),")))}}
                };
                internal static readonly Dictionary<string, Func<IFilterBackend, IGeneratedFilter>> MetadataFactories = new(StringComparer.Ordinal)
                {
                    {{string.Join("\n", factories.Select(factory => factory.Replace("(backend),", "(backend, false, false),")))}}
                };
                internal static readonly Dictionary<string, LegacyFilterBinding> Legacy = new(StringComparer.Ordinal)
                {
                    {{string.Join("\n", legacy)}}
                };
                internal static readonly string[] Coverage =
                {
                    {{string.Join(",\n", coverage)}}
                };
            }
            """, Encoding.UTF8));
    }

    private static string Quote(string value) => SymbolDisplay.FormatLiteral(value, true);
}
