using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace FEB.EventSourcing.Snapshots.Generator;

internal sealed record BuildResult(
    SnapshotModel? RootModel,
    ImmutableArray<Diagnostic> Diagnostics,
    ImmutableArray<SnapshotModel> AllModels);