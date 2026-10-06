namespace DealFlow.Core;

/// <summary>What a client submits to POST /deals.</summary>
public sealed record CreateDealRequest(
    string AccountName,
    string Region,
    decimal AmountUsd,
    string? Notes);

public enum DealStatus { Received, Processed }

public enum DealPriority { Low, Medium, High }

/// <summary>
/// The deal as stored in Cosmos DB. Partitioned by <see cref="Id"/> (see docs/adr/0002).
/// </summary>
public sealed record Deal
{
    public required string Id { get; init; }
    public required string AccountName { get; init; }
    public required string Region { get; init; }
    public required decimal AmountUsd { get; init; }
    public string? Notes { get; init; }
    public required DateTimeOffset SubmittedAt { get; init; }

    public DealStatus Status { get; init; } = DealStatus.Received;
    public DealPriority? Priority { get; init; }
    public string? Summary { get; init; }

    /// <summary>"ai" or "rules" — records which enricher produced the result, for telemetry and audits.</summary>
    public string? EnrichedBy { get; init; }
    public DateTimeOffset? ProcessedAt { get; init; }

    public static Deal FromRequest(CreateDealRequest request, TimeProvider clock) => new()
    {
        Id = Guid.NewGuid().ToString("n"),
        AccountName = request.AccountName.Trim(),
        Region = request.Region.Trim().ToUpperInvariant(),
        AmountUsd = request.AmountUsd,
        Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim(),
        SubmittedAt = clock.GetUtcNow(),
    };
}

public static class DealValidator
{
    public const int MaxNotesLength = 4000;
    private static readonly HashSet<string> Regions = ["NA", "LATAM", "EMEA", "APAC"];

    /// <summary>Returns field → error messages; empty when the request is valid.</summary>
    public static Dictionary<string, string[]> Validate(CreateDealRequest? request)
    {
        var errors = new Dictionary<string, string[]>();
        if (request is null)
        {
            errors["body"] = ["Request body is required."];
            return errors;
        }

        if (string.IsNullOrWhiteSpace(request.AccountName))
            errors[nameof(request.AccountName)] = ["Account name is required."];

        if (string.IsNullOrWhiteSpace(request.Region) || !Regions.Contains(request.Region.Trim().ToUpperInvariant()))
            errors[nameof(request.Region)] = [$"Region must be one of: {string.Join(", ", Regions)}."];

        if (request.AmountUsd <= 0)
            errors[nameof(request.AmountUsd)] = ["Amount must be greater than zero."];

        if (request.Notes?.Length > MaxNotesLength)
            errors[nameof(request.Notes)] = [$"Notes cannot exceed {MaxNotesLength} characters."];

        return errors;
    }
}
