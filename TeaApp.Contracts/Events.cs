namespace TeaApp.Contracts;
public record TeaOrderPlaced(string OrderId, string TeaId);
public record TeaOrderBrewing(string OrderId, string TeaId, DateTimeOffset StartedAt);
public record TeaOrderBrewed(string OrderId, bool Success, DateTimeOffset FinishedAt);