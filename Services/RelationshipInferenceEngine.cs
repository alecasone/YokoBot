using Yoko.Bot.Models;

namespace Yoko.Bot.Services;

internal sealed class RelationshipInferenceEngine
{
    public IReadOnlyList<RelationshipEdge> Build(IEnumerable<RelationshipRecord> directRelationships)
    {
        var edges = new Dictionary<(Guid Source, Guid Target, string Type), RelationshipEdge>();
        foreach (var relationship in directRelationships)
        {
            AddPair(
                edges,
                relationship.SourceCharacterId,
                relationship.TargetCharacterId,
                relationship.TypeId,
                isInferred: false,
                relationship.Id,
                ruleId: null,
                explanation: null);
        }

        var changed = true;
        var pass = 0;
        while (changed && pass++ < 64)
        {
            changed = false;
            foreach (var rule in RelationshipCatalog.InferenceRules)
            {
                if (rule.PathTypeIds.Count == 0) continue;
                var snapshot = edges.Values.ToArray();
                var starts = snapshot.Where(edge => TypeEquals(edge.TypeId, rule.PathTypeIds[0])).ToArray();
                foreach (var start in starts)
                {
                    var endpoints = new HashSet<Guid> { start.TargetCharacterId };
                    foreach (var typeId in rule.PathTypeIds.Skip(1))
                    {
                        endpoints = endpoints
                            .SelectMany(characterId => snapshot
                                .Where(edge => edge.SourceCharacterId == characterId && TypeEquals(edge.TypeId, typeId))
                                .Select(edge => edge.TargetCharacterId))
                            .ToHashSet();
                        if (endpoints.Count == 0) break;
                    }

                    foreach (var endpoint in endpoints.Where(id => id != start.SourceCharacterId))
                    {
                        changed |= AddPair(
                            edges,
                            start.SourceCharacterId,
                            endpoint,
                            rule.ResultTypeId,
                            isInferred: true,
                            relationshipId: null,
                            rule.Id,
                            rule.Explanation);
                    }
                }
            }
        }

        return edges.Values
            .OrderBy(edge => edge.IsInferred)
            .ThenBy(edge => edge.TypeId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(edge => edge.TargetCharacterId)
            .ToArray();
    }

    private static bool AddPair(
        IDictionary<(Guid Source, Guid Target, string Type), RelationshipEdge> edges,
        Guid source,
        Guid target,
        string typeId,
        bool isInferred,
        string? relationshipId,
        string? ruleId,
        string? explanation)
    {
        var definition = RelationshipCatalog.Get(typeId);
        if (definition is null || source == target) return false;

        var changed = AddEdge(edges, new RelationshipEdge(
            source, target, definition.Id, isInferred, relationshipId, ruleId, explanation));
        var inverse = RelationshipCatalog.Get(definition.InverseId);
        if (inverse is not null)
            changed |= AddEdge(edges, new RelationshipEdge(
                target, source, inverse.Id, isInferred, relationshipId, ruleId, explanation));
        return changed;
    }

    private static bool AddEdge(
        IDictionary<(Guid Source, Guid Target, string Type), RelationshipEdge> edges,
        RelationshipEdge candidate)
    {
        var key = (candidate.SourceCharacterId, candidate.TargetCharacterId, candidate.TypeId.ToLowerInvariant());
        if (!edges.TryGetValue(key, out var existing))
        {
            edges[key] = candidate;
            return true;
        }

        if (existing.IsInferred && !candidate.IsInferred)
            edges[key] = candidate;
        return false;
    }

    private static bool TypeEquals(string left, string right) =>
        left.Equals(right, StringComparison.OrdinalIgnoreCase);
}
