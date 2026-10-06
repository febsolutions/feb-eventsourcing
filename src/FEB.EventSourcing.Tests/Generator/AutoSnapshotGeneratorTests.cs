using System.Text.RegularExpressions;
using FEB.EventSourcing.Snapshots;
using FEB.EventSourcing.Snapshots.Generator;
using AwesomeAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace FEB.EventSourcing.Tests.Generator;

public class AutoSnapshotGeneratorTests
{
    private sealed record RunResult(
        Compilation OutputCompilation,
        IReadOnlyList<Diagnostic> GeneratorDiagnostics,
        IReadOnlyList<(string HintName, string Source)> GeneratedSources)
    {
        public IEnumerable<Diagnostic> CompilationErrors
            => OutputCompilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error);
    }

    private static RunResult Run(string source)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source);

        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .Append(MetadataReference.CreateFromFile(typeof(AutoSnapshotAttribute).Assembly.Location))
            .Append(MetadataReference.CreateFromFile(typeof(AggregateRoot).Assembly.Location));

        var compilation = CSharpCompilation.Create(
            "GenTests",
            [syntaxTree],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

        var driver = CSharpGeneratorDriver.Create(new AutoSnapshotGenerator());
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var diagnostics);

        var generated = driver
            .RunGenerators(compilation)
            .GetRunResult()
            .Results
            .SelectMany(r => r.GeneratedSources)
            .Select(s => (s.HintName, s.SourceText.ToString()))
            .ToList();

        return new RunResult(outputCompilation, diagnostics, generated);
    }

    private const string Preamble = """
        using System;
        using System.Collections.Generic;
        using FEB.EventSourcing.Snapshots;
        namespace TestNs;
        """;

    [Fact]
    public void Valid_aggregate_generates_compiling_code_without_diagnostics()
    {
        var result = Run(Preamble + """
            [AutoSnapshot]
            public partial class Thing
            {
                public string Name { get; set; } = "";
                public int Count { get; set; }
                public List<string> Tags { get; set; } = new();
            }
            """);

        result.GeneratorDiagnostics.Should().BeEmpty();
        result.CompilationErrors.Should().BeEmpty();
    }

    [Fact]
    public void Aggregate_in_the_global_namespace_generates_compiling_code()
    {
        // Top-level programs and quick samples often declare the first aggregate without
        // a namespace. ToDisplayString() yields "<global namespace>" there, which crashed
        // the generator with an invalid hint name - surfaced only as warning CS8785, i.e.
        // silently no snapshot code, contradicting the fail-closed guarantee.
        var result = Run("""
            using System.Collections.Generic;
            using FEB.EventSourcing.Snapshots;

            [AutoSnapshot]
            public partial class Counter
            {
                public int Value { get; set; }
                public List<string> Tags { get; set; } = new();
            }
            """);

        result.GeneratorDiagnostics.Should().BeEmpty();
        result.CompilationErrors.Should().BeEmpty();
        result.GeneratedSources.Should().Contain(s => s.HintName.Contains("CounterSnapshot"));
    }

    [Fact]
    public void Primitive_list_generates_compiling_copy_code()
    {
        var result = Run(Preamble + """
            [AutoSnapshot]
            public partial class Thing
            {
                public List<int> Numbers { get; set; } = new();
            }
            """);

        result.CompilationErrors.Should().BeEmpty("List<primitive> must not produce element snapshot code");
        var source = result.GeneratedSources.Single(s => s.HintName.Contains("ThingSnapshot")).Source;
        source.Should().NotContain("RestoreFromSnapshot(x)", "elements of primitive lists have no snapshot methods");
    }

    [Fact]
    public void GetOnly_property_reports_ASG002()
    {
        var result = Run(Preamble + """
            [AutoSnapshot]
            public partial class Thing
            {
                public string Name { get; set; } = "";
                public string Computed => Name.ToUpper();
            }
            """);

        result.GeneratorDiagnostics.Should().Contain(d => d.Id == "ASG002");
    }

    [Fact]
    public void GetOnly_property_with_IgnoreSnapshot_is_fine()
    {
        var result = Run(Preamble + """
            [AutoSnapshot]
            public partial class Thing
            {
                public string Name { get; set; } = "";
                [IgnoreSnapshot]
                public string Computed => Name.ToUpper();
            }
            """);

        result.GeneratorDiagnostics.Should().BeEmpty();
        result.CompilationErrors.Should().BeEmpty();
    }

    [Fact]
    public void Unsupported_property_type_reports_ASG004()
    {
        var result = Run(Preamble + """
            [AutoSnapshot]
            public partial class Thing
            {
                public Dictionary<string, int> Lookup { get; set; } = new();
            }
            """);

        result.GeneratorDiagnostics.Should().Contain(d => d.Id == "ASG004");
    }

    [Fact]
    public void Instance_field_reports_ASG005()
    {
        var result = Run(Preamble + """
            [AutoSnapshot]
            public partial class Thing
            {
                private readonly List<string> _hiddenState = new();
                public string Name { get; set; } = "";
            }
            """);

        result.GeneratorDiagnostics.Should().Contain(d => d.Id == "ASG005",
            "state in fields would otherwise be lost silently");
    }

    [Fact]
    public void Instance_field_with_IgnoreSnapshot_is_fine()
    {
        var result = Run(Preamble + """
            [AutoSnapshot]
            public partial class Thing
            {
                [IgnoreSnapshot]
                private readonly object _lock = new();
                public string Name { get; set; } = "";
            }
            """);

        result.GeneratorDiagnostics.Should().BeEmpty();
        result.CompilationErrors.Should().BeEmpty();
    }

    [Fact]
    public void Non_partial_aggregate_reports_ASG009()
    {
        var result = Run(Preamble + """
            [AutoSnapshot]
            public class Thing
            {
                public string Name { get; set; } = "";
            }
            """);

        result.GeneratorDiagnostics.Should().Contain(d => d.Id == "ASG009");
    }

    [Fact]
    public void Base_class_properties_are_included_in_snapshot()
    {
        var result = Run(Preamble + """
            public abstract class ThingBase
            {
                public string BaseState { get; set; } = "";
            }

            [AutoSnapshot]
            public partial class Thing : ThingBase
            {
                public string Name { get; set; } = "";
            }
            """);

        result.GeneratorDiagnostics.Should().BeEmpty();
        result.CompilationErrors.Should().BeEmpty();

        var dto = result.GeneratedSources.Single(s => s.HintName.Contains("ThingSnapshot")).Source;
        dto.Should().Contain("BaseState", "state from in-source base classes must not be missing silently");
    }

    [Fact]
    public void Base_class_property_with_private_setter_reports_ASG002()
    {
        var result = Run(Preamble + """
            public abstract class ThingBase
            {
                public string BaseState { get; private set; } = "";
            }

            [AutoSnapshot]
            public partial class Thing : ThingBase
            {
                public string Name { get; set; } = "";
            }
            """);

        result.GeneratorDiagnostics.Should().Contain(d => d.Id == "ASG002",
            "a private setter of the base class is not reachable from the generated code");
    }

    [Fact]
    public void Shared_nested_type_between_two_aggregates_is_generated_once()
    {
        var result = Run(Preamble + """
            public partial class Shared
            {
                public string Value { get; set; } = "";
            }

            [AutoSnapshot]
            public partial class First
            {
                public Shared? Data { get; set; }
            }

            [AutoSnapshot]
            public partial class Second
            {
                public Shared? Data { get; set; }
            }
            """);

        result.CompilationErrors.Should().BeEmpty("shared nested types must not be generated twice");
        result.GeneratedSources.Count(s => s.HintName.Contains("SharedSnapshot")).Should().Be(1);
    }

    [Fact]
    public void Changing_a_nested_type_changes_the_root_snapshot_version()
    {
        const string v1 = """
            [AutoSnapshot]
            public partial class Thing
            {
                public Part? Part { get; set; }
            }

            public partial class Part
            {
                public string A { get; set; } = "";
            }
            """;

        const string v2 = """
            [AutoSnapshot]
            public partial class Thing
            {
                public Part? Part { get; set; }
            }

            public partial class Part
            {
                public string A { get; set; } = "";
                public string B { get; set; } = "";
            }
            """;

        var versionV1 = ExtractVersion(Run(Preamble + v1), "ThingSnapshot");
        var versionV2 = ExtractVersion(Run(Preamble + v2), "ThingSnapshot");

        versionV2.Should().NotBe(versionV1,
            "a change to a nested type must invalidate the aggregate's snapshot");
    }

    [Fact]
    public void Unchanged_aggregate_keeps_a_stable_snapshot_version()
    {
        const string source = """
            [AutoSnapshot]
            public partial class Thing
            {
                public string Name { get; set; } = "";
            }
            """;

        ExtractVersion(Run(Preamble + source), "ThingSnapshot")
            .Should().Be(ExtractVersion(Run(Preamble + source), "ThingSnapshot"));
    }

    [Fact]
    public void Module_source_is_generated_with_all_metas()
    {
        var result = Run(Preamble + """
            [AutoSnapshot]
            public partial class Thing
            {
                public string Name { get; set; } = "";
            }
            """);

        var module = result.GeneratedSources.Single(s => s.HintName == "SnapshotMetadataModule.g.cs").Source;
        module.Should().Contain("SnapshotMetadataModule_GenTests");
        module.Should().Contain("ThingSnapshotMeta");
    }

    private static int ExtractVersion(RunResult result, string snapshotHint)
    {
        var source = result.GeneratedSources.Single(s => s.HintName.Contains(snapshotHint)).Source;
        var match = Regex.Match(source, @"public int Version => (-?\d+);");
        match.Success.Should().BeTrue();
        return int.Parse(match.Groups[1].Value);
    }
}
