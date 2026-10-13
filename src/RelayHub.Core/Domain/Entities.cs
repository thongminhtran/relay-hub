namespace RelayHub.Core.Domain;

public enum SourceType { Generic = 0, Instagram = 1, Facebook = 2, Telegram = 3, Email = 4 }

public enum DestinationType { Discord = 0, Teams = 1, Email = 2, Telegram = 3, Http = 4 }

public enum MessageDirection { In = 0, Out = 1 }

public enum DeliveryStatus { Pending = 0, Delivered = 1, Retrying = 2, DeadLettered = 3 }

/// <summary>An isolated set of sources, routes and data ("Studio" = real, "Demo" = public sandbox).</summary>
public sealed class Workspace
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public bool IsDemo { get; set; }
    public string RetryProfile { get; set; } = "default";
}

/// <summary>Where messages come from (a website form, an Instagram account, ...).</summary>
public sealed class Source
{
    public int Id { get; set; }
    public int WorkspaceId { get; set; }
    public SourceType Type { get; set; }
    /// <summary>URL-safe name used in /api/v1/hooks/{sourceKey}.</summary>
    public required string Name { get; set; }
    /// <summary>Public part of the stateless signed API key (see ApiKeys).</summary>
    public required string KeyId { get; set; }
    public bool IsEnabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>All messages with one customer on one channel, in and out.</summary>
public sealed class Conversation
{
    public int Id { get; set; }
    public int WorkspaceId { get; set; }
    public int SourceId { get; set; }
    /// <summary>Provider thread/sender id (Instagram user id, email address, ...).</summary>
    public required string ExternalThreadId { get; set; }
    public string? SenderName { get; set; }
    public string? SenderHandle { get; set; }
    public DateTimeOffset? LastInboundAt { get; set; }
    public DateTimeOffset? LastOutboundAt { get; set; }
}

/// <summary>Where RelayHub sends notifications (Discord, Teams, ...).</summary>
public sealed class Destination
{
    public int Id { get; set; }
    public int WorkspaceId { get; set; }
    public DestinationType Type { get; set; }
    public required string Name { get; set; }
    /// <summary>Connector-specific settings as JSON, e.g. {"webhookUrl": "..."}.</summary>
    public string ConfigJson { get; set; } = "{}";
    public bool IsEnabled { get; set; } = true;
}

/// <summary>A rule: events from Source go to Destination using Template.</summary>
public sealed class Route
{
    public const string DefaultTemplate = "**New message from {{name}}** via {{channel}}\n{{text}}";

    public int Id { get; set; }
    public int WorkspaceId { get; set; }
    public int SourceId { get; set; }
    public int DestinationId { get; set; }
    public string Template { get; set; } = DefaultTemplate;
    public string? Filter { get; set; }
    public bool IsEnabled { get; set; } = true;
}

/// <summary>One message: inbound from a customer, or an outbound reply.</summary>
public sealed class Event
{
    public long Id { get; set; }
    public int WorkspaceId { get; set; }
    public int SourceId { get; set; }
    public int? ConversationId { get; set; }
    public MessageDirection Direction { get; set; } = MessageDirection.In;
    /// <summary>Provider message id or Idempotency-Key. Unique per source = idempotency.</summary>
    public required string ExternalId { get; set; }
    public required string Channel { get; set; }
    public string? SenderName { get; set; }
    public string? SenderHandle { get; set; }
    public string Text { get; set; } = "";
    public string PayloadJson { get; set; } = "{}";
    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;
    public required string CorrelationId { get; set; }
    public string? ProviderMessageId { get; set; }
    public List<Delivery> Deliveries { get; set; } = [];
}

/// <summary>One tracked send of an event to one destination.</summary>
public sealed class Delivery
{
    public long Id { get; set; }
    public long EventId { get; set; }
    public int DestinationId { get; set; }
    /// <summary>The route whose template is rendered on every attempt (retries and replays re-render). Null if the route was deleted.</summary>
    public int? RouteId { get; set; }
    public DeliveryStatus Status { get; set; } = DeliveryStatus.Pending;
    public int AttemptCount { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public string? LastError { get; set; }
    public List<DeliveryAttempt> Attempts { get; set; } = [];
}

public sealed class DeliveryAttempt
{
    public long Id { get; set; }
    public long DeliveryId { get; set; }
    public int AttemptNo { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public int DurationMs { get; set; }
    public int? HttpStatus { get; set; }
    public string? Error { get; set; }
}
