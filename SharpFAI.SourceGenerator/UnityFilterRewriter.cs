using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SharpFAI.SourceGenerator;

internal sealed class UnityFilterRewriter : CSharpSyntaxRewriter
{
    private static readonly Dictionary<string, string> Types = new(StringComparer.Ordinal)
    {
        ["Shader"] = "IFilterShader",
        ["Material"] = "IFilterMaterial",
        ["Texture2D"] = "IFilterTexture",
        ["RenderTexture"] = "IFilterTarget",
        ["Color"] = "Vector4",
    };

    private static readonly Dictionary<string, string> Members = new(StringComparer.Ordinal)
    {
        ["Shader.Find"] = "backend.FindShader",
        ["Resources.Load"] = "backend.LoadTexture",
        ["RenderTexture.GetTemporary"] = "backend.RentTarget",
        ["RenderTexture.ReleaseTemporary"] = "backend.ReleaseTarget",
        ["Graphics.Blit"] = "backend.Blit",
        ["Object.DestroyImmediate"] = "backend.ReleaseMaterial",
        ["Time.deltaTime"] = "backend.DeltaTime",
        ["Screen.width"] = "backend.ScreenWidth",
        ["Screen.height"] = "backend.ScreenHeight",
        ["Application.isPlaying"] = "backend.IsPlaying",
        ["Mathf.Round"] = "MathF.Round",
        ["Mathf.Min"] = "Math.Min",
        ["Mathf.Max"] = "Math.Max",
        ["Mathf.Clamp"] = "Math.Clamp",
        ["Mathf.Clamp01"] = "Clamp01",
        ["Color.white"] = "Vector4.One",
        ["Color.clear"] = "Vector4.Zero",
        ["Color.black"] = "new Vector4(0f, 0f, 0f, 1f)",
        ["Color.red"] = "new Vector4(1f, 0f, 0f, 1f)",
        ["Color.green"] = "new Vector4(0f, 1f, 0f, 1f)",
        ["Color.blue"] = "new Vector4(0f, 0f, 1f, 1f)",
        ["Vector2.zero"] = "Vector2.Zero",
        ["Vector2.one"] = "Vector2.One",
        ["FilterMode.Point"] = "FilterSampling.Point",
        ["FilterMode.Bilinear"] = "FilterSampling.Bilinear",
        ["FilterMode.Trilinear"] = "FilterSampling.Trilinear",
    };

    public string Member { private get; set; } = "";

    public override SyntaxNode? VisitAttributeList(AttributeListSyntax node) => null;

    public override SyntaxNode VisitIdentifierName(IdentifierNameSyntax node) =>
        SyntaxFacts.IsInTypeOnlyContext(node) && Types.TryGetValue(node.Identifier.ValueText, out string? type)
            ? SyntaxFactory.IdentifierName(type) : node;

    public override SyntaxNode VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
    {
        if (Members.TryGetValue(node.ToString(), out string? replacement))
            return SyntaxFactory.ParseExpression(replacement);

        if (node.Expression is IdentifierNameSyntax identifier &&
            (identifier.Identifier.ValueText is "UnityEngine" or "Graphics" or "Shader" or "Resources" or "Time" or
             "RenderTexture" or "Mathf" or "Object" or "SystemInfo" or "Application" or "Screen"))
            throw new InvalidOperationException(Member + ": unsupported Unity reference " + node);

        string member = node.Name.Identifier.ValueText;
        string? property = member switch
        {
            "width" => "Width",
            "height" => "Height",
            "filterMode" => "Sampling",
            "x" or "r" => "X",
            "y" or "g" => "Y",
            "z" or "b" => "Z",
            "w" or "a" => "W",
            "SetColor" => "SetVector",
            _ => null,
        };
        if (property is not null)
            return node.WithExpression((ExpressionSyntax)Visit(node.Expression)!)
                .WithName(SyntaxFactory.IdentifierName(property));

        return base.VisitMemberAccessExpression(node)!;
    }

    public override SyntaxNode VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
    {
        if (node.Type.ToString() == "Material" && node.ArgumentList?.Arguments.Count == 1)
            return SyntaxFactory.InvocationExpression(SyntaxFactory.ParseExpression("backend.CreateMaterial"),
                (ArgumentListSyntax)Visit(node.ArgumentList)!);

        if (node.Type.ToString() == "Color" && node.ArgumentList is { Arguments.Count: 3 } color)
            node = node.WithArgumentList(color.AddArguments(SyntaxFactory.Argument(SyntaxFactory.ParseExpression("1f"))));

        if (node.Type.ToString() is not ("Vector2" or "Vector4" or "Color"))
            throw new InvalidOperationException(Member + ": unsupported constructor " + node.Type);
        return base.VisitObjectCreationExpression(node)!;
    }

    public override SyntaxNode? VisitExpressionStatement(ExpressionStatementSyntax node)
    {
        if (node.Expression is AssignmentExpressionSyntax assignment &&
            assignment.Left is MemberAccessExpressionSyntax access && access.Name.Identifier.ValueText == "hideFlags" &&
            assignment.Right.ToString() == "HideFlags.HideAndDontSave")
            return null;

        if (node.Expression is InvocationExpressionSyntax call &&
            call.Expression.ToString() == "Object.DestroyImmediate" && call.ArgumentList.Arguments.Count == 1)
        {
            ExpressionSyntax target = call.ArgumentList.Arguments[0].Expression;
            return SyntaxFactory.Block(
                (StatementSyntax)base.VisitExpressionStatement(node)!,
                SyntaxFactory.ExpressionStatement(SyntaxFactory.AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression, target,
                    SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression))));
        }

        return base.VisitExpressionStatement(node);
    }

    public override SyntaxNode VisitCastExpression(CastExpressionSyntax node)
    {
        if (node.Type.ToString() == "bool")
            return SyntaxFactory.ParenthesizedExpression(SyntaxFactory.BinaryExpression(SyntaxKind.NotEqualsExpression,
                (ExpressionSyntax)Visit(node.Expression)!, SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression)));
        return base.VisitCastExpression(node)!;
    }

}
