using System;
using System.Collections.Generic;
using System.Linq;
using TiaOpennessMcpServer.Models;

namespace TiaOpennessMcpServer.Utilities;

public static class V1PlanApplicability
{
    public const string Applicable = "applicable";
    public const string NotApplicable = "not_applicable";
    public const string Unsupported = "unsupported";
}

public sealed record V1RepresentationPlanStep
{
    public required string Format { get; init; }
    public required string Applicability { get; init; }
    public string? Reason { get; init; }

    public bool IsApplicable => string.Equals(
        Applicability,
        V1PlanApplicability.Applicable,
        StringComparison.Ordinal);
}

public static class V1RepresentationPolicy
{
    public static IReadOnlyList<V1RepresentationPlanStep> CreatePlan(
        string objectType,
        string? language,
        string? requestedFormat)
    {
        var format = NormalizeFormat(requestedFormat);
        var category = ClassifyObject(objectType);

        if (string.Equals(format, V1RepresentationFormats.Best, StringComparison.Ordinal))
            return CreateBestPlan(category, language);

        return new[] { CreateStrictStep(category, language, format) };
    }

    public static string NormalizeFormat(string? requestedFormat)
    {
        if (string.IsNullOrWhiteSpace(requestedFormat))
            return V1RepresentationFormats.Best;

        var normalized = requestedFormat!.Trim().ToLowerInvariant();
        if (normalized == V1RepresentationFormats.Best ||
            normalized == V1RepresentationFormats.SimaticSd ||
            normalized == V1RepresentationFormats.SclSource ||
            normalized == V1RepresentationFormats.SimaticMl)
        {
            return normalized;
        }

        throw UnsupportedFormat(requestedFormat);
    }

    public static bool CanFallback(string? requestedFormat, string attemptResult)
    {
        if (attemptResult is null)
            throw new ArgumentNullException(nameof(attemptResult));

        return string.Equals(NormalizeFormat(requestedFormat), V1RepresentationFormats.Best, StringComparison.Ordinal) &&
               !string.Equals(attemptResult, V1AttemptResults.Succeeded, StringComparison.Ordinal);
    }

    public static bool IsPureSclBlock(string objectType, string? language)
    {
        var category = ClassifyObject(objectType);
        return category == ObjectCategory.CodeBlock &&
               string.Equals(language?.Trim(), "SCL", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<V1RepresentationPlanStep> CreateBestPlan(
        ObjectCategory category,
        string? language)
    {
        switch (category)
        {
            case ObjectCategory.CodeBlock:
                var blockSteps = new List<V1RepresentationPlanStep>
                {
                    Applicable(V1RepresentationFormats.SimaticSd),
                };
                if (string.Equals(language?.Trim(), "SCL", StringComparison.OrdinalIgnoreCase))
                    blockSteps.Add(Applicable(V1RepresentationFormats.SclSource));
                blockSteps.Add(Applicable(V1RepresentationFormats.SimaticMl));
                return blockSteps;

            case ObjectCategory.DataBlock:
            case ObjectCategory.PlcDataType:
                return new[]
                {
                    Applicable(V1RepresentationFormats.SimaticSd),
                    Applicable(V1RepresentationFormats.SimaticMl),
                };

            case ObjectCategory.TagTable:
                return new[] { Applicable(V1RepresentationFormats.SimaticMl) };

            default:
                throw new InvalidOperationException("Unexpected representation-policy category.");
        }
    }

    private static V1RepresentationPlanStep CreateStrictStep(
        ObjectCategory category,
        string? language,
        string format)
    {
        if (format == V1RepresentationFormats.SimaticMl)
            return Applicable(format);

        if (format == V1RepresentationFormats.SimaticSd)
        {
            return category == ObjectCategory.TagTable
                ? Unsupported(format, "PLC tag tables do not expose native SIMATIC SD in version one.")
                : Applicable(format);
        }

        if (format == V1RepresentationFormats.SclSource)
        {
            if (category != ObjectCategory.CodeBlock)
                return Unsupported(format, "Raw SCL applies only to an OB, FB, or FC block.");

            return string.Equals(language?.Trim(), "SCL", StringComparison.OrdinalIgnoreCase)
                ? Applicable(format)
                : NotApplicable(format, "The selected block is not a pure SCL block.");
        }

        throw UnsupportedFormat(format);
    }

    private static ObjectCategory ClassifyObject(string objectType)
    {
        if (string.IsNullOrWhiteSpace(objectType))
            throw UnsupportedObject(objectType ?? string.Empty);

        var key = new string(objectType.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        switch (key)
        {
            case "OB":
            case "ORGANIZATIONBLOCK":
            case "ORGANISATIONBLOCK":
            case "FB":
            case "FUNCTIONBLOCK":
            case "FC":
            case "FUNCTION":
                return ObjectCategory.CodeBlock;

            case "DB":
            case "DATABLOCK":
            case "GLOBALDB":
            case "GLOBALDATABLOCK":
            case "INSTANCEDB":
            case "INSTANCEDATABLOCK":
            case "ARRAYDB":
            case "ARRAYDATABLOCK":
                return ObjectCategory.DataBlock;

            case "UDT":
            case "PLCTYPE":
            case "PLCDATATYPE":
            case "DATATYPE":
                return ObjectCategory.PlcDataType;

            case "TAGTABLE":
            case "PLCTAGTABLE":
                return ObjectCategory.TagTable;

            default:
                throw UnsupportedObject(objectType);
        }
    }

    private static V1RepresentationPlanStep Applicable(string format) => new()
    {
        Format = format,
        Applicability = V1PlanApplicability.Applicable,
    };

    private static V1RepresentationPlanStep NotApplicable(string format, string reason) => new()
    {
        Format = format,
        Applicability = V1PlanApplicability.NotApplicable,
        Reason = reason,
    };

    private static V1RepresentationPlanStep Unsupported(string format, string reason) => new()
    {
        Format = format,
        Applicability = V1PlanApplicability.Unsupported,
        Reason = reason,
    };

    private static V1BridgeException UnsupportedFormat(string format) => new(new V1Error
    {
        Code = V1ErrorCodes.UnsupportedFormat,
        Message = $"Unsupported representation format '{format}'.",
    });

    private static V1BridgeException UnsupportedObject(string objectType) => new(new V1Error
    {
        Code = V1ErrorCodes.UnsupportedObject,
        Message = $"Object type '{objectType}' is not a version-one content object.",
    });

    private enum ObjectCategory
    {
        CodeBlock,
        DataBlock,
        PlcDataType,
        TagTable,
    }
}
