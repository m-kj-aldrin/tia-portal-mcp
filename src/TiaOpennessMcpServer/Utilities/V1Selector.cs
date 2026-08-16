using System;
using System.Collections.Generic;
using System.Linq;
using TiaOpennessMcpServer.Models;

namespace TiaOpennessMcpServer.Utilities;

public static class V1Selector
{
    public static T ResolvePlc<T>(
        IEnumerable<T> candidates,
        V1PlcSelector selector,
        Func<T, V1PlcIdentity> identitySelector)
    {
        if (candidates is null)
            throw new ArgumentNullException(nameof(candidates));
        if (selector is null)
            throw new ArgumentNullException(nameof(selector));
        if (identitySelector is null)
            throw new ArgumentNullException(nameof(identitySelector));
        if (IsBlank(selector.ObjectId) && IsBlank(selector.Path) && IsBlank(selector.Name))
            throw InvalidSelector("A PLC selector requires objectId, path, or name.");

        var identified = candidates
            .Select(candidate => new Identified<T, V1PlcIdentity>(candidate, identitySelector(candidate)))
            .ToArray();

        IEnumerable<Identified<T, V1PlcIdentity>> matches;
        if (!IsBlank(selector.ObjectId))
        {
            matches = identified.Where(item =>
                string.Equals(item.Identity.ObjectId, selector.ObjectId, StringComparison.Ordinal));
        }
        else
        {
            matches = identified.Where(item =>
                (IsBlank(selector.Path) || PathEquals(item.Identity.Path, selector.Path)) &&
                (IsBlank(selector.Name) || TextEquals(item.Identity.Name, selector.Name)));
        }

        var materialized = matches.ToArray();
        if (materialized.Length == 1)
            return materialized[0].Value;

        if (materialized.Length == 0)
        {
            throw new V1BridgeException(new V1Error
            {
                Code = V1ErrorCodes.ObjectNotFound,
                Message = "No PLC matched the requested selector.",
            });
        }

        throw new V1BridgeException(new V1Error
        {
            Code = V1ErrorCodes.AmbiguousSelector,
            Message = $"The PLC selector matched {materialized.Length} PLCs.",
            Candidates = materialized.Select(item => ToCandidate(item.Identity)).ToArray(),
        });
    }

    public static T ResolveObject<T>(
        IEnumerable<T> candidates,
        string requestedPlcObjectId,
        V1ObjectSelector selector,
        Func<T, V1ObjectIdentity> identitySelector)
    {
        if (candidates is null)
            throw new ArgumentNullException(nameof(candidates));
        if (string.IsNullOrWhiteSpace(requestedPlcObjectId))
            throw new ArgumentException("The requested PLC object identifier is required.", nameof(requestedPlcObjectId));
        if (selector is null)
            throw new ArgumentNullException(nameof(selector));
        if (identitySelector is null)
            throw new ArgumentNullException(nameof(identitySelector));
        if (IsBlank(selector.ObjectId) && IsBlank(selector.Path) && IsBlank(selector.Name))
            throw InvalidSelector("An object selector requires objectId, path, or name.");

        var identified = candidates
            .Select(candidate => new Identified<T, V1ObjectIdentity>(candidate, identitySelector(candidate)))
            .Where(item => string.Equals(
                item.Identity.PlcObjectId,
                requestedPlcObjectId,
                StringComparison.Ordinal))
            .ToArray();

        IEnumerable<Identified<T, V1ObjectIdentity>> matches;
        if (!IsBlank(selector.ObjectId))
        {
            matches = identified.Where(item =>
                string.Equals(item.Identity.ObjectId, selector.ObjectId, StringComparison.Ordinal));
        }
        else
        {
            matches = identified.Where(item =>
                (IsBlank(selector.Path) || PathEquals(item.Identity.Path, selector.Path)) &&
                (IsBlank(selector.Name) || TextEquals(item.Identity.Name, selector.Name)) &&
                (IsBlank(selector.Type) || TextEquals(item.Identity.Type, selector.Type)));
        }

        var materialized = matches.ToArray();
        if (materialized.Length == 1)
            return materialized[0].Value;

        if (materialized.Length == 0)
        {
            throw new V1BridgeException(new V1Error
            {
                Code = V1ErrorCodes.ObjectNotFound,
                Message = "No object in the requested PLC matched the selector.",
            });
        }

        throw new V1BridgeException(new V1Error
        {
            Code = V1ErrorCodes.AmbiguousSelector,
            Message = $"The object selector matched {materialized.Length} objects in the requested PLC.",
            Candidates = materialized.Select(item => ToCandidate(item.Identity)).ToArray(),
        });
    }

    private static bool IsBlank(string? value) => string.IsNullOrWhiteSpace(value);

    private static bool TextEquals(string? left, string? right) =>
        string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool PathEquals(string? left, string? right) =>
        string.Equals(NormalizeHierarchyPath(left), NormalizeHierarchyPath(right), StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeHierarchyPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        return path!.Trim().Replace('\\', '/').Trim('/');
    }

    private static V1SelectionCandidate ToCandidate(V1PlcIdentity identity) => new()
    {
        ObjectId = identity.ObjectId,
        Path = identity.Path,
        Name = identity.Name,
        Type = "PLC",
    };

    private static V1SelectionCandidate ToCandidate(V1ObjectIdentity identity) => new()
    {
        ObjectId = identity.ObjectId,
        PlcObjectId = identity.PlcObjectId,
        Path = identity.Path,
        Name = identity.Name,
        Type = identity.Type,
    };

    private static V1BridgeException InvalidSelector(string message) => new(new V1Error
    {
        Code = V1ErrorCodes.InvalidSelector,
        Message = message,
    });

    private sealed class Identified<TValue, TIdentity>
    {
        public Identified(TValue value, TIdentity identity)
        {
            Value = value;
            Identity = identity ?? throw new InvalidOperationException("A selector identity cannot be null.");
        }

        public TValue Value { get; }
        public TIdentity Identity { get; }
    }
}
