using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FEB.EventSourcing.Snapshots.Generator;

internal sealed class ModelCollector
{
    private readonly ImmutableArray<Diagnostic>.Builder _diags;
    private readonly Dictionary<string, SnapshotModel> _modelsByKey = new();
    private readonly Stack<string> _stack = new(); // for the cycle path

    public ModelCollector(ImmutableArray<Diagnostic>.Builder diags) => _diags = diags;

    public ImmutableArray<SnapshotModel> Models => _modelsByKey.Values.ToImmutableArray();

    public SnapshotModel? BuildModelForType(INamedTypeSymbol type, bool isRootAggregate, Location? rootLocation)
    {
        // Key: fully qualified name incl. generics (sufficient for nested types)
        var key = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        // Cycle?
        if (_stack.Contains(key))
        {
            _diags.Add(Diagnostic.Create(
                Diagnostics.CyclicReference,
                rootLocation ?? Location.None,
                string.Join(" -> ", _stack.Reverse().Select(x => x.Split('.').Last())) + " -> " + type.Name));
            return null;
        }

        // Bereits gebaut?
        if (_modelsByKey.TryGetValue(key, out var existing))
            return existing;

        // ONLY classes / records need a SnapshotModel
        if (type.TypeKind != TypeKind.Class && !type.IsRecord)
        {
            // primitive / struct / enum -> no SnapshotModel needed
            return null;
        }

        var declarations = type.DeclaringSyntaxReferences
            .Select(r => r.GetSyntax())
            .OfType<ClassDeclarationSyntax>()
            .ToImmutableArray();

        // External types (no syntax) -> error
        if (declarations.Length == 0)
        {
            _diags.Add(Diagnostic.Create(
                Diagnostics.UnsupportedPropertyType,
                rootLocation ?? Location.None,
                type.Name,
                type.ToDisplayString()));
            return null;
        }

        var isPartial = declarations.Any(d => d.Modifiers.Any(m => m.Text == "partial"));
        if (!isPartial)
        {
            _diags.Add(Diagnostic.Create(
                Diagnostics.TypeNotPartial,
                declarations[0].Identifier.GetLocation(),
                type.Name));
            return null;
        }


        _stack.Push(key);

        // The global namespace displays as "<global namespace>", which is neither a valid
        // hint name nor emittable C#; downstream code treats "" as "no namespace".
        var ns = type.ContainingNamespace is { IsGlobalNamespace: false } containing
            ? containing.ToDisplayString()
            : "";
        var name = type.Name;
        var snapshotTypeName = $"{name}Snapshot";

        var propsBuilder = ImmutableArray.CreateBuilder<SnapshotProperty>();
        var seenNames = new HashSet<string>();

        // Fail-closed: the type itself AND all base classes declared in source are
        // collected. External base classes (e.g. AggregateRoot from the framework)
        // end the chain - their state (Version etc.) is managed by the framework.
        foreach (var declaringType in GetSourceDeclaredTypeChain(type))
        {
            var isBaseType = !SymbolEqualityComparer.Default.Equals(declaringType, type);

            ReportInstanceFields(declaringType);

            foreach (var p in declaringType.GetMembers().OfType<IPropertySymbol>())
            {
                // Overrides/shadowing: the most derived declaration wins
                if (!seenNames.Add(p.Name))
                    continue;

                if (!TryBuildSnapshotProperty(p, declaringType.Name, isBaseType, rootLocation, out var prop))
                    continue;

                propsBuilder.Add(prop!);
            }
        }

        var props = propsBuilder.ToImmutable();
        var version = ComputeSnapshotVersion(name, props);

        var model = new SnapshotModel(ns, name, snapshotTypeName, props, version);
        _modelsByKey[key] = model;

        _stack.Pop();
        return model;
    }

    /// <summary>
    /// Returns the type itself and all base classes as long as they are source code in
    /// the current compilation. The first external type (metadata reference) ends the chain.
    /// </summary>
    private static IEnumerable<INamedTypeSymbol> GetSourceDeclaredTypeChain(INamedTypeSymbol type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (current.SpecialType == SpecialType.System_Object)
                yield break;

            if (current.DeclaringSyntaxReferences.Length == 0)
                yield break;

            yield return current;
        }
    }

    /// <summary>
    /// Fail-closed: instance fields cannot be snapshotted and therefore must not
    /// exist silently - either model them as a property or mark them with
    /// [IgnoreSnapshot] as deliberately transient.
    /// </summary>
    private void ReportInstanceFields(INamedTypeSymbol type)
    {
        foreach (var field in type.GetMembers().OfType<IFieldSymbol>())
        {
            if (field.IsImplicitlyDeclared || field.IsStatic || field.IsConst)
                continue;

            if (HasAttribute(field, "IgnoreSnapshotAttribute"))
                continue;

            _diags.Add(Diagnostic.Create(
                Diagnostics.InstanceFieldNotSupported,
                field.Locations.FirstOrDefault() ?? Location.None,
                field.Name,
                type.Name));
        }
    }

    private static int ComputeSnapshotVersion(
        string aggregateName,
        ImmutableArray<SnapshotProperty> props)
    {
        // Recursive: the version of nested models is part of the signature,
        // so changes to nested types invalidate the snapshot too.
        var signature = aggregateName + "|" +
                        string.Join("|", props.Select(PropertySignature));

        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(signature));

        return BitConverter.ToInt32(hash, 0);

        static string PropertySignature(SnapshotProperty p)
            => p.NestedModel is null
                ? $"{p.Name}:{p.TypeName}"
                : $"{p.Name}:{p.TypeName}#{p.NestedModel.Version}";
    }

    private bool TryBuildSnapshotProperty(IPropertySymbol p, string owningTypeName, bool declaredOnBaseType, Location? rootLocation, out SnapshotProperty? result)
    {
        result = null;

        if (p.IsStatic || p.Parameters.Length > 0)
            return false;

        if (HasAttribute(p, "IgnoreSnapshotAttribute"))
            return false;

        if (p.GetMethod is null || p.SetMethod is null)
        {
            _diags.Add(Diagnostic.Create(
                Diagnostics.PropertyNotWritable,
                p.Locations.FirstOrDefault() ?? Location.None,
                p.Name,
                owningTypeName));
            return false;
        }

        var rawType = UnwrapNullable(p.Type, out var isNullable);

        // The setter must be reachable from the generated code:
        // - on the type itself (partial): private/internal/public
        // - on a base class: protected/internal/public (private would be unreachable)
        var setterAccessibility = p.SetMethod.DeclaredAccessibility;
        var setterReachable = declaredOnBaseType
            ? setterAccessibility is Accessibility.Protected or Accessibility.ProtectedOrInternal
                or Accessibility.Internal or Accessibility.Public
            : setterAccessibility is Accessibility.Private or Accessibility.Internal or Accessibility.Public;

        if (!setterReachable)
        {
            _diags.Add(Diagnostic.Create(
                Diagnostics.PropertyNotWritable,
                p.Locations.FirstOrDefault() ?? Location.None,
                p.Name,
                owningTypeName));
            return false;
        }

        // 1) Direct types
        if (IsDirectSupportedType(rawType))
        {
            result = new SnapshotProperty(
                p.Name,
                p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                SnapshotKind.Direct,
                null,
                null,
                isNullable);
            return true;
        }

        // 2) List<T>
        if (rawType is INamedTypeSymbol nts && nts.IsGenericType && (nts.Name is "List" or "IReadOnlyList" or "ICollection"))
        {
            var elementType = nts.TypeArguments[0];
            if (elementType is not INamedTypeSymbol elementNamed)
            {
                _diags.Add(Diagnostic.Create(
                    Diagnostics.UnsupportedPropertyType,
                    p.Locations.FirstOrDefault() ?? Location.None,
                    p.Name,
                    owningTypeName,
                    p.Type.ToDisplayString()));
                return false;
            }

            // The element must be snapshottable -> we generate a model for it if it is a class/record
            if (IsDirectSupportedType(elementType))
            {
                // List<primitive> -> copy without element snapshots
                result = new SnapshotProperty(
                    p.Name,
                    p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    SnapshotKind.List,
                    null,
                    elementType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    isNullable);
                return true;
            }

            // Element is a class/record -> nested snapshot model
            var nested = BuildModelForType(elementNamed, isRootAggregate: false, rootLocation);
            if (nested is null) return false;

            result = new SnapshotProperty(
                p.Name,
                p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                SnapshotKind.List,
                nested,
                elementType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                isNullable);
            return true;
        }

        // 3) Object (class/record)
        if (rawType is INamedTypeSymbol named && (named.TypeKind == TypeKind.Class || named.IsRecord))
        {
            var nested = BuildModelForType(named, isRootAggregate: false, rootLocation);
            if (nested is null) return false;

            result = new SnapshotProperty(
                p.Name,
                p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                SnapshotKind.Object,
                nested,
                null,
                isNullable);
            return true;
        }

        // 4) unsupported -> ASG004
        _diags.Add(Diagnostic.Create(
            Diagnostics.UnsupportedPropertyType,
            p.Locations.FirstOrDefault() ?? Location.None,
            p.Name,
            owningTypeName,
            p.Type.ToDisplayString()));

        return false;
    }

    private static bool HasAttribute(ISymbol symbol, string name)
        => symbol.GetAttributes().Any(a => a.AttributeClass?.Name == name);


    private static bool IsDirectSupportedType(ITypeSymbol type)
    {
        if (type.SpecialType != SpecialType.None) return true;
        if (type.TypeKind == TypeKind.Enum) return true;
        return type.Name is "Guid" or "DateTime" or "DateTimeOffset";
    }

    private static ITypeSymbol UnwrapNullable(ITypeSymbol type, out bool isNullable)
    {
        isNullable = false;

        // Nullable<T>
        if (type is INamedTypeSymbol nts &&
            nts.IsGenericType &&
            nts.Name == "Nullable")
        {
            isNullable = true;
            return nts.TypeArguments[0];
        }

        // Reference type with ?
        if (type.NullableAnnotation == NullableAnnotation.Annotated)
        {
            isNullable = true;
            return type;
        }

        return type;
    }
}
